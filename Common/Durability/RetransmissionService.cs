using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Feed;

namespace MarketData.Common.Durability
{
    public enum RetransmissionStatus : ushort
    {
        Success = 0,
        SnapshotRequired = 1,
        WrongSession = 2,
        InvalidRequest = 3,
        CorruptJournal = 4,
    }

    /// <summary>Bounded TCP gap-fill service over an exact journal prefix.</summary>
    public sealed class RetransmissionService : IDisposable
    {
        public const uint Magic = 0x32585452; // RTX2
        public const ushort Version = 2;
        public const int MaxRangeLength = 10_000;
        public const int RequestSize = 36;
        public const int ResponseSize = 24;
        public const int FrameHeaderSize = 20;
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private readonly TcpListener _listener;
        private readonly ulong _sessionId;
        private readonly JournalRangeReader _rangeReader;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _slots;
        private readonly object _clientGate = new();
        private readonly HashSet<Task> _clients = new();

        private Task _accepting;
        private long _requestsServed;
        private long _requestsRefused;
        private long _messagesSent;
        private int _started;
        private int _disposed;

        public RetransmissionService(string journalDirectory, int port = 0,
            IPAddress address = null, int maxConcurrentRequests = 8)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
            if ((uint)port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (maxConcurrentRequests is < 1 or > 1024)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));

            var report = JournalReader.Recover(journalDirectory);
            if (report.Outcome == RecoveryOutcome.Corrupt || report.SessionId == 0)
                throw new InvalidDataException("Retransmission requires a valid journal session.");

            _sessionId = report.SessionId;
            _rangeReader = new JournalRangeReader(journalDirectory);
            _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
            _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ulong SessionId => _sessionId;
        public long RequestsServed => Interlocked.Read(ref _requestsServed);
        public long RequestsRefused => Interlocked.Read(ref _requestsRefused);
        public long MessagesSent => Interlocked.Read(ref _messagesSent);

        public void Start()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("Retransmission service is already running.");

            _listener.Start(backlog: 128);
            _accepting = AcceptLoopAsync(_shutdown.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _slots.WaitAsync(token).ConfigureAwait(false);
                    TcpClient client;

                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    }
                    catch
                    {
                        _slots.Release();
                        throw;
                    }

                    var task = ServeAndReleaseAsync(client, token);
                    lock (_clientGate)
                        _clients.Add(task);
                    _ = RemoveWhenCompleteAsync(task);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task RemoveWhenCompleteAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            finally
            {
                lock (_clientGate)
                    _clients.Remove(task);
            }
        }

        private async Task ServeAndReleaseAsync(TcpClient client, CancellationToken serviceToken)
        {
            try
            {
                using (client)
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken))
                {
                    timeout.CancelAfter(RequestTimeout);
                    await ServeAsync(client, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or
                                           OperationCanceledException)
            {
                // A failed recovery connection does not affect the live feed.
            }
            finally
            {
                _slots.Release();
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken token)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var request = new byte[RequestSize];
            await ReadExactlyAsync(stream, request, token).ConfigureAwait(false);

            if (BinaryPrimitives.ReadUInt32LittleEndian(request) != Magic ||
                BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)) != Version ||
                BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(32)) !=
                    Crc32C.Compute(request.AsSpan(0, 32)))
            {
                await RefuseAsync(stream, RetransmissionStatus.InvalidRequest, _sessionId, token)
                    .ConfigureAwait(false);
                return;
            }

            var requestedSession = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(8));
            var from = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(16));
            var to = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));

            if (requestedSession != 0 && requestedSession != _sessionId)
            {
                await RefuseAsync(stream, RetransmissionStatus.WrongSession, _sessionId, token)
                    .ConfigureAwait(false);
                return;
            }

            if (to < from || to - from >= MaxRangeLength)
            {
                await RefuseAsync(stream, RetransmissionStatus.InvalidRequest, _sessionId, token)
                    .ConfigureAwait(false);
                return;
            }

            JournalRangeResult result;
            List<SequencedPayload> messages;

            try
            {
                result = _rangeReader.TryRead(_sessionId, from, to, out messages);
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                await RefuseAsync(stream, RetransmissionStatus.CorruptJournal, _sessionId, token)
                    .ConfigureAwait(false);
                return;
            }

            var status = result switch
            {
                JournalRangeResult.Success => RetransmissionStatus.Success,
                JournalRangeResult.WrongSession => RetransmissionStatus.WrongSession,
                JournalRangeResult.Corrupt => RetransmissionStatus.CorruptJournal,
                _ => RetransmissionStatus.SnapshotRequired,
            };

            if (status != RetransmissionStatus.Success)
            {
                await RefuseAsync(stream, status, _sessionId, token).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _requestsServed);
            await WriteResponseHeaderAsync(stream, status, _sessionId, messages.Count, token)
                .ConfigureAwait(false);

            foreach (var message in messages)
            {
                var frame = new byte[FrameHeaderSize];
                BinaryPrimitives.WriteUInt64LittleEndian(frame, message.Sequence);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), message.MessageCount);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), 0);
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), message.Payload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16),
                    Crc32C.Compute(frame.AsSpan(0, 16), message.Payload));

                await stream.WriteAsync(frame, token).ConfigureAwait(false);
                await stream.WriteAsync(message.Payload, token).ConfigureAwait(false);
                Interlocked.Increment(ref _messagesSent);
            }

            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task RefuseAsync(NetworkStream stream, RetransmissionStatus status,
            ulong sessionId, CancellationToken token)
        {
            Interlocked.Increment(ref _requestsRefused);
            await WriteResponseHeaderAsync(stream, status, sessionId, 0, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task WriteResponseHeaderAsync(NetworkStream stream,
            RetransmissionStatus status, ulong sessionId, int count, CancellationToken token)
        {
            var response = new byte[ResponseSize];
            BinaryPrimitives.WriteUInt32LittleEndian(response, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6), (ushort)status);
            BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), sessionId);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16), count);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20),
                Crc32C.Compute(response.AsSpan(0, 20)));
            await stream.WriteAsync(response, token).ConfigureAwait(false);
        }

        internal static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
            CancellationToken token)
        {
            var read = 0;

            while (read < buffer.Length)
            {
                var got = await stream.ReadAsync(buffer.Slice(read), token).ConfigureAwait(false);
                if (got == 0)
                    throw new IOException("Peer closed a partial frame.");
                read += got;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _shutdown.Cancel();
            _listener.Stop();

            try { _accepting?.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }

            Task[] clients;
            lock (_clientGate)
                clients = new List<Task>(_clients).ToArray();

            try { Task.WaitAll(clients, TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }

            _slots.Dispose();
            _shutdown.Dispose();
        }
    }

    public sealed record RetransmissionResponse(RetransmissionStatus Status, ulong SessionId,
        List<SequencedPayload> Messages);

    public interface IRetransmissionClient
    {
        Task<RetransmissionResponse> RequestDetailedAsync(ulong sessionId, ulong from, ulong to,
            CancellationToken token = default);
    }

    public sealed class RetransmissionClient : IRetransmissionClient
    {
        private readonly IPEndPoint _endpoint;

        public RetransmissionClient(int port, IPAddress address = null)
            => _endpoint = new IPEndPoint(address ?? IPAddress.Loopback, port);

        public async Task<List<SequencedPayload>> RequestAsync(ulong from, ulong to,
            CancellationToken token = default)
        {
            var response = await RequestDetailedAsync(0, from, to, token).ConfigureAwait(false);
            return response.Status == RetransmissionStatus.Success ? response.Messages : null;
        }

        public async Task<List<SequencedPayload>> RequestAsync(ulong sessionId, ulong from, ulong to,
            CancellationToken token = default)
        {
            var response = await RequestDetailedAsync(sessionId, from, to, token).ConfigureAwait(false);
            return response.Status == RetransmissionStatus.Success ? response.Messages : null;
        }

        public async Task<RetransmissionResponse> RequestDetailedAsync(ulong sessionId, ulong from,
            ulong to, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RetransmissionService.RequestTimeout);
            token = timeout.Token;

            using var client = new TcpClient();
            await client.ConnectAsync(_endpoint, token).ConfigureAwait(false);
            client.NoDelay = true;
            var stream = client.GetStream();

            var request = new byte[RetransmissionService.RequestSize];
            BinaryPrimitives.WriteUInt32LittleEndian(request, RetransmissionService.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), RetransmissionService.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), sessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), from);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), to);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(32),
                Crc32C.Compute(request.AsSpan(0, 32)));
            await stream.WriteAsync(request, token).ConfigureAwait(false);

            var header = new byte[RetransmissionService.ResponseSize];
            await RetransmissionService.ReadExactlyAsync(stream, header, token).ConfigureAwait(false);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != RetransmissionService.Magic ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) !=
                    RetransmissionService.Version ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) !=
                    Crc32C.Compute(header.AsSpan(0, 20)))
                throw new IOException("Retransmission response header failed validation.");

            var status = (RetransmissionStatus)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var responseSession = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));

            if (!Enum.IsDefined(status) || count < 0 || count > RetransmissionService.MaxRangeLength ||
                responseSession == 0 ||
                (status != RetransmissionStatus.Success && count != 0) ||
                (status == RetransmissionStatus.Success && count == 0) ||
                (sessionId != 0 && responseSession != sessionId &&
                    status != RetransmissionStatus.WrongSession))
                throw new IOException("Retransmission response is inconsistent.");

            var messages = new List<SequencedPayload>(count);
            var cursor = from;
            var coveredThrough = from;

            for (var i = 0; i < count; i++)
            {
                var frame = new byte[RetransmissionService.FrameHeaderSize];
                await RetransmissionService.ReadExactlyAsync(stream, frame, token).ConfigureAwait(false);
                var sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame);
                var messageCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10));
                var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(12));
                var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16));

                if (messageCount == 0 || flags != 0 || length < 0 ||
                    length > JournalRecord.MaxPayloadSize || sequence != cursor ||
                    sequence > ulong.MaxValue - messageCount)
                    throw new IOException("Retransmission frame is invalid.");

                coveredThrough = sequence + messageCount - 1;
                if (coveredThrough > to)
                    throw new IOException("Retransmission response exceeds the requested range.");

                var payload = new byte[length];
                await RetransmissionService.ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);
                if (storedCrc != Crc32C.Compute(frame.AsSpan(0, 16), payload))
                    throw new IOException("Retransmission frame checksum failed.");

                messages.Add(new SequencedPayload(sequence, 0, payload)
                {
                    SessionId = responseSession,
                    MessageCount = messageCount,
                });

                cursor = coveredThrough == ulong.MaxValue ? ulong.MaxValue : coveredThrough + 1;
            }

            if (status == RetransmissionStatus.Success && coveredThrough != to)
                throw new IOException("Retransmission response does not cover the requested range.");

            return new RetransmissionResponse(status, responseSession, messages);
        }
    }
}
