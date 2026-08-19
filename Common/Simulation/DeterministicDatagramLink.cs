using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MarketData.Common.Simulation
{
    public readonly record struct PacketFault(
        long Transmission,
        bool Drop = false,
        int DelayTicks = 0,
        int Duplicates = 0,
        int DuplicateSpacingTicks = 0,
        int CorruptOffset = -1,
        byte CorruptMask = 1);

    public sealed class PacketFaultPlan
    {
        private readonly Dictionary<long, PacketFault> _faults;

        public PacketFaultPlan(IEnumerable<PacketFault> faults)
        {
            ArgumentNullException.ThrowIfNull(faults);
            _faults = new Dictionary<long, PacketFault>();

            foreach (var fault in faults)
            {
                if (fault.Transmission < 0 || fault.DelayTicks < 0 ||
                    fault.Duplicates is < 0 or > DeterministicDatagramLink.MaxDuplicates ||
                    fault.DuplicateSpacingTicks < 0 || fault.CorruptOffset < -1 ||
                    (fault.CorruptOffset >= 0 && fault.CorruptMask == 0) ||
                    (fault.Drop && (fault.Duplicates != 0 || fault.CorruptOffset >= 0)))
                    throw new ArgumentException("Packet fault is invalid.", nameof(faults));

                if (!_faults.TryAdd(fault.Transmission, fault))
                    throw new ArgumentException("A transmission has more than one fault.", nameof(faults));
            }
        }

        public static PacketFaultPlan None { get; } = new(Array.Empty<PacketFault>());

        internal bool TryGet(long transmission, out PacketFault fault)
            => _faults.TryGetValue(transmission, out fault);
    }

    public enum PacketSendResult : byte
    {
        Scheduled,
        Dropped,
        QueueOverflow,
    }

    public readonly record struct DeliveredDatagram(
        long Transmission,
        long DeliveryTick,
        ReadOnlyMemory<byte> Payload);

    /// <summary>Single-threaded virtual datagram link with explicit fault schedules.</summary>
    public sealed class DeterministicDatagramLink
    {
        public const int MaxDuplicates = 32;

        private readonly PacketFaultPlan _plan;
        private readonly PriorityQueue<ScheduledDatagram, (long Tick, long Ordinal)> _queue = new();
        private readonly int _maxQueuedPackets;
        private readonly int _maxPacketBytes;
        private long _transmissions;
        private long _ordinal;
        private long _lastSendTick = -1;

        public DeterministicDatagramLink(PacketFaultPlan plan = null,
            int maxQueuedPackets = 4_096, int maxPacketBytes = 65_535)
        {
            if (maxQueuedPackets < 1)
                throw new ArgumentOutOfRangeException(nameof(maxQueuedPackets));
            if (maxPacketBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPacketBytes));

            _plan = plan ?? PacketFaultPlan.None;
            _maxQueuedPackets = maxQueuedPackets;
            _maxPacketBytes = maxPacketBytes;
        }

        public long CurrentTick { get; private set; }
        public int QueuedPackets => _queue.Count;
        public long Transmissions => _transmissions;
        public long ScheduledPackets { get; private set; }
        public long DeliveredPackets { get; private set; }
        public long DroppedPackets { get; private set; }
        public long OverflowPackets { get; private set; }
        public long CorruptedPackets { get; private set; }
        public long DuplicatePackets { get; private set; }

        public PacketSendResult Send(ReadOnlySpan<byte> packet, long sentAtTick)
        {
            if (packet.Length == 0 || packet.Length > _maxPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(packet));
            if (sentAtTick < 0 || sentAtTick < _lastSendTick || sentAtTick < CurrentTick)
                throw new ArgumentOutOfRangeException(nameof(sentAtTick));
            if (_transmissions == long.MaxValue)
                throw new InvalidOperationException("The transmission sequence is exhausted.");

            var transmission = _transmissions;
            if (!_plan.TryGet(transmission, out var fault))
                fault = new PacketFault(transmission);

            if (fault.CorruptOffset >= packet.Length)
                throw new ArgumentOutOfRangeException(nameof(packet),
                    "The corruption offset is outside this packet.");

            var copies = 0;
            var firstDelivery = 0L;

            if (!fault.Drop)
            {
                copies = checked(fault.Duplicates + 1);
                firstDelivery = checked(sentAtTick + fault.DelayTicks);
                _ = checked(firstDelivery + (long)(copies - 1) * fault.DuplicateSpacingTicks);

                if (_ordinal > long.MaxValue - copies)
                    throw new InvalidOperationException("The delivery sequence is exhausted.");
            }

            _lastSendTick = sentAtTick;
            _transmissions++;

            if (fault.Drop)
            {
                DroppedPackets++;
                return PacketSendResult.Dropped;
            }

            if (_queue.Count > _maxQueuedPackets - copies)
            {
                OverflowPackets += copies;
                return PacketSendResult.QueueOverflow;
            }

            for (var copy = 0; copy < copies; copy++)
            {
                var payload = packet.ToArray();
                if (fault.CorruptOffset >= 0)
                {
                    payload[fault.CorruptOffset] ^= fault.CorruptMask;
                    CorruptedPackets++;
                }

                var deliveryTick = checked(firstDelivery + (long)copy * fault.DuplicateSpacingTicks);
                var scheduled = new ScheduledDatagram(transmission, deliveryTick, payload);
                _queue.Enqueue(scheduled, (deliveryTick, _ordinal++));

                if (copy > 0)
                    DuplicatePackets++;
            }

            ScheduledPackets += copies;
            return PacketSendResult.Scheduled;
        }

        public bool TryDeliverNext(out DeliveredDatagram datagram)
        {
            if (!_queue.TryDequeue(out var scheduled, out _))
            {
                datagram = default;
                return false;
            }

            CurrentTick = Math.Max(CurrentTick, scheduled.DeliveryTick);
            DeliveredPackets++;
            datagram = new DeliveredDatagram(scheduled.Transmission, scheduled.DeliveryTick,
                scheduled.Payload);
            return true;
        }

        public async ValueTask DrainAsync(Func<DeliveredDatagram, ValueTask> receiver)
        {
            ArgumentNullException.ThrowIfNull(receiver);

            while (TryDeliverNext(out var datagram))
                await receiver(datagram).ConfigureAwait(false);
        }

        private readonly record struct ScheduledDatagram(
            long Transmission,
            long DeliveryTick,
            byte[] Payload);
    }
}
