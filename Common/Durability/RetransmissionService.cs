using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MarketData.Common.Durability
{
    /// <summary>
    /// Serves gap-fill requests out of the journal, over TCP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a separate channel from the live feed, and deliberately reliable where the
    /// feed is not. This mirrors how real venues do it, and the reason is not tradition: a
    /// subscriber asking for a retransmission has already lost packets, so answering on the same
    /// lossy multicast group it just lost them on is a poor bet. TCP also gives per-subscriber
    /// flow control, which matters because a recovering subscriber wants a burst of history while
    /// everyone else wants the live feed uninterrupted.
    /// </para>
    /// <para>
    /// The service is intentionally cheap to refuse. Retransmission is where a struggling
    /// subscriber can turn a local problem into a publisher-wide one, so a request for more than
    /// <see cref="MaxRangeLength"/> messages is rejected outright rather than served slowly. A
    /// subscriber that far behind should recover from a snapshot instead, which is O(book) rather
    /// than O(history).
    /// </para>
    /// </remarks>
    public sealed class RetransmissionService : IDisposable
    {
        /// <summary>Largest gap that will be filled from history rather than by snapshot.</summary>
        public const int MaxRangeLength = 10_000;

        public const int RequestSize = 16;

        private readonly TcpListener _listener;
        private readonly string _journalDirectory;
        private readonly CancellationTokenSource _shutdown = new();
        private Task _accepting;
        private int _disposed;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public long RequestsServed { get; private set; }
        public long RequestsRefused { get; private set; }
        public long MessagesSent { get; private set; }

        public RetransmissionService(string journalDirectory, int port = 0, IPAddress address = null)
        {
            _journalDirectory = journalDirectory;
            _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        }

        public void Start()
        {
            _listener.Start();
            _accepting = AcceptLoopAsync(_shutdown.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                // Each request is served on its own task so one slow recovering subscriber cannot
                // block the queue behind it.
                _ = ServeAsync(client, token);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();

                    var request = new byte[RequestSize];
                    await ReadExactlyAsync(stream, request, token).ConfigureAwait(false);

                    var from = BinaryPrimitives.ReadUInt64LittleEndian(request);
                    var to = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(8));

                    if (to < from || to - from + 1 > MaxRangeLength)
                    {
                        RequestsRefused++;
                        await WriteRefusalAsync(stream, token).ConfigureAwait(false);
                        return;
                    }

                    var messages = JournalReader.ReadRange(_journalDirectory, from, to);
                    RequestsServed++;

                    var count = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(count, messages.Count);
                    await stream.WriteAsync(count, token).ConfigureAwait(false);

                    foreach (var message in messages)
                    {
                        var frame = new byte[12];
                        BinaryPrimitives.WriteUInt64LittleEndian(frame, message.Sequence);
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), message.Payload.Length);

                        await stream.WriteAsync(frame, token).ConfigureAwait(false);
                        await stream.WriteAsync(message.Payload, token).ConfigureAwait(false);
                        MessagesSent++;
                    }

                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    // Shutdown, not a failure.
                }
                catch (IOException)
                {
                    // The recovering subscriber gave up. Its problem, not ours.
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        /// <summary>A refusal is an empty range: "recover from a snapshot instead".</summary>
        private static async Task WriteRefusalAsync(NetworkStream stream, CancellationToken token)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, -1);
            await stream.WriteAsync(count, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        internal static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
        {
            var read = 0;

            while (read < buffer.Length)
            {
                var got = await stream.ReadAsync(buffer.Slice(read), token).ConfigureAwait(false);

                if (got == 0)
                    throw new IOException("Peer closed before the message was complete.");

                read += got;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _shutdown.Cancel();

            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            try
            {
                _accepting?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _shutdown.Dispose();
        }
    }

    /// <summary>Client side of gap fill.</summary>
    public sealed class RetransmissionClient
    {
        private readonly IPEndPoint _endpoint;

        public RetransmissionClient(int port, IPAddress address = null)
            => _endpoint = new IPEndPoint(address ?? IPAddress.Loopback, port);

        /// <summary>
        /// Requests <c>[from, to]</c>.
        /// </summary>
        /// <returns>
        /// The recovered messages, or null when the publisher refused - which means the gap is too
        /// large to fill from history and the subscriber should wait for the next snapshot.
        /// </returns>
        public async Task<List<SequencedPayload>> RequestAsync(ulong from, ulong to,
            CancellationToken token = default)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_endpoint, token).ConfigureAwait(false);
            client.NoDelay = true;

            var stream = client.GetStream();

            var request = new byte[RetransmissionService.RequestSize];
            BinaryPrimitives.WriteUInt64LittleEndian(request, from);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), to);
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var countBuffer = new byte[4];
            await RetransmissionService.ReadExactlyAsync(stream, countBuffer, token).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);

            if (count < 0)
                return null;

            var messages = new List<SequencedPayload>(count);

            for (var i = 0; i < count; i++)
            {
                var frame = new byte[12];
                await RetransmissionService.ReadExactlyAsync(stream, frame, token).ConfigureAwait(false);

                var sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame);
                var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8));

                if (length < 0 || length > JournalRecord.MaxPayloadSize)
                    throw new IOException($"Retransmission declared an implausible length of {length}.");

                var payload = new byte[length];
                await RetransmissionService.ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);

                messages.Add(new SequencedPayload(sequence, 0, payload));
            }

            return messages;
        }
    }
}
