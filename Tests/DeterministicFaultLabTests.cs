using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Feed;
using MarketData.Common.Simulation;
using Xunit;

namespace MarketData.Tests
{
    public sealed class DeterministicFaultLabTests
    {
        private const ulong Session = 0xFA017ABUL;
        private const int Instrument = 1;

        [Fact]
        public async Task ScriptedMixedFaultsConvergeThroughExactRepair()
        {
            var faults = new PacketFaultPlan(new[]
            {
                new PacketFault(5, CorruptOffset: FeedProtocol.HeaderSize +
                    FeedProtocol.IncrementalSize - 1),
                new PacketFault(10, Drop: true),
                new PacketFault(20, DelayTicks: 10),
                new PacketFault(30, Duplicates: 2, DuplicateSpacingTicks: 1),
                new PacketFault(40, DelayTicks: 5),
            });

            var outcome = await RunScenarioAsync(64, faults);

            Assert.Equal(4, outcome.Client.Requests);
            Assert.Equal(1, outcome.Link.DroppedPackets);
            Assert.Equal(1, outcome.Link.CorruptedPackets);
            Assert.Equal(2, outcome.Link.DuplicatePackets);
            Assert.True(outcome.Decoder.Statistics.Duplicates >= 4);
            Assert.Equal(1, outcome.Decoder.Statistics.IntegrityFailures);
            Assert.Equal(0, outcome.Decoder.Statistics.Gaps);
        }

        [Fact]
        public async Task SeededMixedFaultSchedulesNeverProduceSilentDivergence()
        {
            for (var seed = 1; seed <= 40; seed++)
            {
                var faults = RandomFaults(seed, transmissions: 128);

                try
                {
                    await RunScenarioAsync(128, faults);
                }
                catch (Exception error)
                {
                    throw new Xunit.Sdk.XunitException($"fault seed {seed}: {error}");
                }
            }
        }

        [Fact]
        public async Task DeliveryUsesVirtualTimeAndStableTieBreaking()
        {
            var link = new DeterministicDatagramLink(new PacketFaultPlan(new[]
            {
                new PacketFault(0, DelayTicks: 3, Duplicates: 1,
                    DuplicateSpacingTicks: 2, CorruptOffset: 1, CorruptMask: 0x80),
                new PacketFault(1, Drop: true),
            }));

            link.Send(new byte[] { 1, 2, 3 }, 0);
            link.Send(new byte[] { 4 }, 1);
            link.Send(new byte[] { 5 }, 2);

            var delivered = new List<DeliveredDatagram>();
            await link.DrainAsync(datagram =>
            {
                delivered.Add(datagram);
                return ValueTask.CompletedTask;
            });

            Assert.Equal(new long[] { 2, 0, 0 }, delivered.Select(item => item.Transmission));
            Assert.Equal(new long[] { 2, 3, 5 }, delivered.Select(item => item.DeliveryTick));
            Assert.Equal((byte)0x82, delivered[1].Payload.Span[1]);
            Assert.Equal(3, link.DeliveredPackets);
            Assert.Equal(1, link.DroppedPackets);
            Assert.Equal(2, link.CorruptedPackets);
        }

        [Fact]
        public void QueueAdmissionIsAtomicAndBounded()
        {
            var link = new DeterministicDatagramLink(new PacketFaultPlan(new[]
            {
                new PacketFault(0, Duplicates: 2),
            }), maxQueuedPackets: 2);

            Assert.Equal(PacketSendResult.QueueOverflow, link.Send(new byte[] { 1 }, 0));
            Assert.Equal(0, link.QueuedPackets);
            Assert.Equal(3, link.OverflowPackets);
            Assert.False(link.TryDeliverNext(out _));
        }

        [Fact]
        public void RejectedSendDoesNotConsumeTransmissionOrClockState()
        {
            var link = new DeterministicDatagramLink(new PacketFaultPlan(new[]
            {
                new PacketFault(0, CorruptOffset: 2, CorruptMask: 0x40),
            }));

            Assert.Throws<ArgumentOutOfRangeException>(() => link.Send(new byte[] { 1 }, 7));
            Assert.Equal(0, link.Transmissions);
            Assert.Equal(0, link.CurrentTick);
            Assert.Equal(0, link.QueuedPackets);

            Assert.Equal(PacketSendResult.Scheduled, link.Send(new byte[] { 1, 2, 3 }, 6));
            Assert.True(link.TryDeliverNext(out var delivered));
            Assert.Equal(6, delivered.DeliveryTick);
            Assert.Equal((byte)0x43, delivered.Payload.Span[2]);
            Assert.Equal(1, link.Transmissions);
        }

        private static async Task<ScenarioOutcome> RunScenarioAsync(int messageCount,
            PacketFaultPlan faults)
        {
            var packets = new SortedDictionary<ulong, byte[]> { [0] = SnapshotPacket(0) };

            for (var sequence = 1; sequence <= messageCount; sequence++)
                packets[(ulong)sequence] = IncrementalPacket((ulong)sequence, -sequence);

            var terminalSequence = (ulong)messageCount + 1;
            packets[terminalSequence] = HeartbeatPacket(terminalSequence);

            var client = new InMemoryRetransmissionClient(Session, packets);
            var decoder = new FeedDecoder(_ => new SortedArrayBook(messageCount + 1));
            var coordinator = new FeedRecoveryCoordinator(decoder, client);
            var link = new DeterministicDatagramLink(faults,
                maxQueuedPackets: checked(packets.Count * 4),
                maxPacketBytes: FeedProtocol.MaxPacketSize);

            foreach (var packet in packets)
            {
                var result = link.Send(packet.Value, checked((long)packet.Key));
                Assert.NotEqual(PacketSendResult.QueueOverflow, result);
            }

            await link.DrainAsync(async datagram =>
            {
                await coordinator.ConsumeAsync(datagram.Payload,
                    TestContext.Current.CancellationToken);
            });

            Assert.False(decoder.IsStale);
            Assert.Equal(terminalSequence + 1, decoder.ExpectedSequence);
            Assert.Equal(messageCount, decoder.BookFor(Instrument).Count(Side.Bid));
            Assert.Equal(Enumerable.Range(1, messageCount).Select(value => -value),
                decoder.BookFor(Instrument).ToList(Side.Bid).Select(level => level.Price));

            return new ScenarioOutcome(link, decoder, client);
        }

        private static PacketFaultPlan RandomFaults(int seed, int transmissions)
        {
            var random = new Random(seed);
            var faults = new List<PacketFault>();

            for (var transmission = 1; transmission <= transmissions; transmission++)
            {
                var mode = random.Next(100);

                if (mode < 8)
                {
                    faults.Add(new PacketFault(transmission, Drop: true));
                }
                else if (mode < 16)
                {
                    faults.Add(new PacketFault(transmission,
                        DelayTicks: random.Next(0, 8),
                        CorruptOffset: FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize - 1,
                        CorruptMask: (byte)(1 << random.Next(8))));
                }
                else
                {
                    var delay = random.Next(0, 8);
                    var duplicates = mode < 26 ? random.Next(1, 3) : 0;

                    if (delay != 0 || duplicates != 0)
                    {
                        faults.Add(new PacketFault(transmission,
                            DelayTicks: delay,
                            Duplicates: duplicates,
                            DuplicateSpacingTicks: duplicates == 0 ? 0 : random.Next(0, 3)));
                    }
                }
            }

            return new PacketFaultPlan(faults);
        }

        private static byte[] SnapshotPacket(ulong sequence)
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.SnapshotSize(0, 0)];
            FeedProtocol.WriteSnapshot(packet.AsSpan(FeedProtocol.HeaderSize), Instrument,
                ReadOnlySpan<PriceLevel>.Empty, ReadOnlySpan<PriceLevel>.Empty);
            FeedProtocol.WriteHeader(packet, 1, Session, sequence, checked((long)sequence));
            return packet;
        }

        private static byte[] IncrementalPacket(ulong sequence, int price)
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize];
            FeedProtocol.WriteIncremental(packet.AsSpan(FeedProtocol.HeaderSize), FeedMessageType.Add,
                Instrument, Side.Bid, new PriceLevel(price, 100));
            FeedProtocol.WriteHeader(packet, 1, Session, sequence, checked((long)sequence));
            return packet;
        }

        private static byte[] HeartbeatPacket(ulong sequence)
        {
            var packet = new byte[FeedProtocol.HeaderSize + 1];
            packet[FeedProtocol.HeaderSize] = (byte)FeedMessageType.Heartbeat;
            FeedProtocol.WriteHeader(packet, 1, Session, sequence, checked((long)sequence));
            return packet;
        }

        private sealed class InMemoryRetransmissionClient : IRetransmissionClient
        {
            private readonly ulong _session;
            private readonly IReadOnlyDictionary<ulong, byte[]> _packets;

            public InMemoryRetransmissionClient(ulong session,
                IReadOnlyDictionary<ulong, byte[]> packets)
            {
                _session = session;
                _packets = packets;
            }

            public int Requests { get; private set; }

            public Task<RetransmissionResponse> RequestDetailedAsync(ulong sessionId, ulong from,
                ulong to, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                Requests++;

                if (sessionId != _session)
                    return Response(RetransmissionStatus.WrongSession);
                if (to < from)
                    return Response(RetransmissionStatus.InvalidRequest);

                var messages = new List<SequencedPayload>();
                var cursor = from;

                while (cursor <= to)
                {
                    if (!_packets.TryGetValue(cursor, out var packet) ||
                        !FeedProtocol.TryReadHeader(packet, out var header, out _) ||
                        header.SessionId != _session || header.FirstSequence != cursor)
                        return Response(RetransmissionStatus.SnapshotRequired);

                    var end = checked(cursor + header.MessageCount - 1);
                    if (end > to)
                        return Response(RetransmissionStatus.SnapshotRequired);

                    messages.Add(new SequencedPayload(cursor, header.SourceTimestamp, packet)
                    {
                        SessionId = _session,
                        MessageCount = header.MessageCount,
                    });

                    if (end == ulong.MaxValue)
                        break;
                    cursor = end + 1;
                }

                return Task.FromResult(new RetransmissionResponse(
                    RetransmissionStatus.Success, _session, messages));
            }

            private Task<RetransmissionResponse> Response(RetransmissionStatus status)
                => Task.FromResult(new RetransmissionResponse(status, _session,
                    new List<SequencedPayload>()));
        }

        private sealed record ScenarioOutcome(
            DeterministicDatagramLink Link,
            FeedDecoder Decoder,
            InMemoryRetransmissionClient Client);
    }
}
