using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Durability;

namespace MarketData.Common.Feed
{
    public enum GapRecoveryResult : byte
    {
        NotNeeded,
        Repaired,
        SnapshotRequired,
        InvalidPacket,
    }

    /// <summary>Serializes live packets with exact unicast gap fill.</summary>
    public sealed class FeedRecoveryCoordinator
    {
        private readonly FeedDecoder _decoder;
        private readonly RetransmissionClient _client;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _repairing;

        public FeedRecoveryCoordinator(FeedDecoder decoder, RetransmissionClient client)
        {
            _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public bool IsRepairing => Volatile.Read(ref _repairing) != 0;

        public async ValueTask<GapRecoveryResult> ConsumeAsync(ReadOnlyMemory<byte> packet,
            CancellationToken token = default)
        {
            if (!FeedProtocol.TryReadHeader(packet.Span, out var header, out _))
            {
                _decoder.Consume(packet.Span);
                return GapRecoveryResult.InvalidPacket;
            }

            await _gate.WaitAsync(token).ConfigureAwait(false);

            try
            {
                var activeSession = _decoder.SessionId;
                var expected = _decoder.ExpectedSequence;
                var missing = activeSession == header.SessionId && expected < header.FirstSequence;

                if (missing)
                    Interlocked.Exchange(ref _repairing, 1);

                _decoder.Consume(packet.Span);

                if (!missing)
                    return GapRecoveryResult.NotNeeded;

                var response = await _client.RequestDetailedAsync(header.SessionId, expected,
                    header.FirstSequence - 1, token).ConfigureAwait(false);

                if (response.Status != RetransmissionStatus.Success ||
                    !ApplyExactPrefix(response.Messages, header.SessionId, expected,
                        header.FirstSequence))
                {
                    _decoder.FlushGaps();
                    return GapRecoveryResult.SnapshotRequired;
                }

                return _decoder.HeldPackets == 0 && _decoder.ExpectedSequence >=
                    header.FirstSequence + header.MessageCount
                    ? GapRecoveryResult.Repaired
                    : GapRecoveryResult.SnapshotRequired;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or SocketException or
                                           OperationCanceledException)
            {
                _decoder.FlushGaps();
                return GapRecoveryResult.SnapshotRequired;
            }
            finally
            {
                Interlocked.Exchange(ref _repairing, 0);
                _gate.Release();
            }
        }

        public async ValueTask FlushGapsAsync(CancellationToken token = default)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);

            try { _decoder.FlushGaps(); }
            finally { _gate.Release(); }
        }

        private bool ApplyExactPrefix(System.Collections.Generic.List<SequencedPayload> packets,
            ulong sessionId, ulong expected, ulong resume)
        {
            var cursor = expected;

            foreach (var packet in packets)
            {
                if (!FeedProtocol.TryReadHeader(packet.Payload, out var header, out _) ||
                    header.SessionId != sessionId || header.FirstSequence != cursor ||
                    packet.Sequence != cursor || packet.MessageCount != header.MessageCount)
                    return false;

                _decoder.Consume(packet.Payload);
                cursor = header.FirstSequence + header.MessageCount;
            }

            return cursor == resume;
        }
    }
}
