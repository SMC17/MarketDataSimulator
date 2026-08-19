using System;
using System.Threading;

namespace MarketData.Common.Availability
{
    /// <summary>The role a node believes it holds.</summary>
    public enum NodeRole : byte
    {
        /// <summary>Following the primary; may serve reads, must not publish.</summary>
        Backup = 0,

        /// <summary>Publishing. Exactly one node may hold this per epoch.</summary>
        Primary = 1,

        /// <summary>Withdrawn. Neither publishing nor eligible.</summary>
        Stopped = 2,
    }

    public enum PromotionOutcome : byte
    {
        Promoted,

        /// <summary>The lease had not expired; the incumbent is still within its term.</summary>
        IncumbentStillLive,

        /// <summary>A higher epoch already exists: this node is stale and must not publish.</summary>
        Fenced,

        /// <summary>This node has not caught up far enough to take over safely.</summary>
        NotCaughtUp,
    }

    public sealed record PromotionResult(PromotionOutcome Outcome, ulong Epoch, ulong FromSequence)
    {
        public bool Succeeded => Outcome == PromotionOutcome.Promoted;
    }

    /// <summary>
    /// Allocates fencing tokens that are unique across the whole cluster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the part that cannot be done locally, and saying so is more useful than pretending
    /// otherwise. If each node incremented its own counter, five isolated nodes would all promote
    /// to "epoch 1" and fencing would compare equal tokens - which is no fencing at all. The whole
    /// mechanism rests on the epoch being globally unique and monotonic.
    /// </para>
    /// <para>
    /// In a real deployment this is backed by whatever already provides consensus: an etcd lease,
    /// a ZooKeeper sequential node, a database sequence. The point of the interface is that the
    /// coordinator does not care which, and that the dependency is explicit rather than assumed
    /// away.
    /// </para>
    /// </remarks>
    public interface IEpochAllocator
    {
        /// <summary>Returns a token strictly greater than every token previously returned.</summary>
        ulong NextEpoch();
    }

    /// <summary>
    /// An in-process allocator, for a single-process deployment and for tests.
    /// </summary>
    /// <remarks>
    /// Correct only when every candidate shares this object. Across processes it provides no
    /// mutual exclusion whatsoever, which is exactly why the production implementation has to come
    /// from a consensus store.
    /// </remarks>
    public sealed class InMemoryEpochAllocator : IEpochAllocator
    {
        private long _epoch;

        public ulong NextEpoch() => (ulong)Interlocked.Increment(ref _epoch);
    }

    /// <summary>
    /// Decides who publishes, and makes it impossible for two nodes to believe it at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this exists to prevent is split brain: a primary that is merely slow, not dead,
    /// is declared dead, a backup promotes, and two nodes publish under the same sequence numbers.
    /// Subscribers cannot detect that - both streams are internally consistent - so books diverge
    /// silently and stay diverged. It is strictly worse than an outage, because an outage is
    /// visible.
    /// </para>
    /// <para>
    /// A heartbeat timeout alone cannot prevent it, and no timeout length fixes that: the network
    /// cannot distinguish a dead peer from an unreachable one. What does prevent it is a
    /// <b>fencing token</b> - the epoch. Every promotion increments it, every published packet
    /// carries it, and a node that learns of a higher epoch stops publishing immediately. A
    /// resurrected primary therefore cannot do damage: its first contact with the newer world
    /// tells it that it lost, and its packets are rejected by anything that has already seen the
    /// higher epoch.
    /// </para>
    /// <para>
    /// The second guard is the catch-up bar. A backup that promotes while behind would restart the
    /// sequence below what subscribers have already applied, which reissues numbers - the exact
    /// corruption the sequencer refuses elsewhere. So promotion is refused unless the candidate has
    /// replicated to within <see cref="MaxPromotionLagRecords"/> of the last known sequence.
    /// </para>
    /// </remarks>
    public sealed class FailoverCoordinator
    {
        /// <summary>How far behind a candidate may be and still be allowed to take over.</summary>
        public const ulong MaxPromotionLagRecords = 0;

        private readonly object _gate = new();
        private readonly TimeSpan _leaseDuration;
        private readonly Func<long> _clock;
        private readonly IEpochAllocator _epochs;

        private ulong _epoch;
        private long _leaseExpiresAt;
        private string _leaseHolder;

        public FailoverCoordinator(string nodeId, TimeSpan leaseDuration,
            Func<long> monotonicClockMs = null, IEpochAllocator epochAllocator = null)
        {
            NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));

            if (leaseDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));

            _leaseDuration = leaseDuration;
            _clock = monotonicClockMs ?? (() => Environment.TickCount64);

            // Defaulting to a private allocator is safe only for a lone node. Candidates that can
            // promote against each other must be given a shared one, or their tokens collide and
            // fencing silently stops working.
            _epochs = epochAllocator ?? new InMemoryEpochAllocator();
        }

        public string NodeId { get; }
        public NodeRole Role { get; private set; } = NodeRole.Backup;

        /// <summary>The fencing token. Monotonic; every promotion increments it.</summary>
        public ulong Epoch
        {
            get { lock (_gate) return _epoch; }
        }

        /// <summary>Highest sequence this node has durably replicated.</summary>
        public ulong ReplicatedSequence { get; private set; }

        /// <summary>Highest sequence this node believes exists anywhere.</summary>
        public ulong KnownSequence { get; private set; }

        public long Promotions { get; private set; }
        public long FencedAttempts { get; private set; }

        public event Action<ulong> Fenced;

        /// <summary>Records replication progress.</summary>
        public void RecordReplicated(ulong sequence)
        {
            lock (_gate)
            {
                if (sequence > ReplicatedSequence)
                    ReplicatedSequence = sequence;

                if (sequence > KnownSequence)
                    KnownSequence = sequence;
            }
        }

        /// <summary>Records that a sequence exists, whether or not this node holds it yet.</summary>
        public void ObserveSequence(ulong sequence)
        {
            lock (_gate)
            {
                if (sequence > KnownSequence)
                    KnownSequence = sequence;
            }
        }

        /// <summary>The primary renews its lease; also how a backup learns the primary is alive.</summary>
        public void Heartbeat(string fromNodeId, ulong epoch, ulong sequence)
        {
            lock (_gate)
            {
                if (epoch < _epoch)
                    return;   // a stale heartbeat proves nothing

                if (epoch > _epoch)
                {
                    // Somebody else won an election we did not know about.
                    _epoch = epoch;

                    if (Role == NodeRole.Primary && fromNodeId != NodeId)
                    {
                        Role = NodeRole.Backup;
                        FencedAttempts++;
                        Fenced?.Invoke(epoch);
                    }
                }

                _leaseHolder = fromNodeId;
                _leaseExpiresAt = _clock() + (long)_leaseDuration.TotalMilliseconds;

                if (sequence > KnownSequence)
                    KnownSequence = sequence;
            }
        }

        /// <summary>Whether the incumbent's lease has run out.</summary>
        public bool LeaseExpired
        {
            get { lock (_gate) return _leaseHolder is null || _clock() >= _leaseExpiresAt; }
        }

        /// <summary>
        /// Attempts to take over.
        /// </summary>
        /// <remarks>
        /// Deliberately refuses more often than it accepts. Every refusal path here is a case where
        /// promoting would produce two publishers or a rewound sequence, both of which corrupt
        /// subscribers silently.
        /// </remarks>
        public PromotionResult TryPromote()
        {
            lock (_gate)
            {
                if (Role == NodeRole.Stopped)
                    return new PromotionResult(PromotionOutcome.Fenced, _epoch, ReplicatedSequence);

                if (_leaseHolder is not null && _leaseHolder != NodeId && _clock() < _leaseExpiresAt)
                    return new PromotionResult(PromotionOutcome.IncumbentStillLive, _epoch, ReplicatedSequence);

                if (KnownSequence - ReplicatedSequence > MaxPromotionLagRecords)
                {
                    return new PromotionResult(PromotionOutcome.NotCaughtUp, _epoch, ReplicatedSequence);
                }

                var allocated = _epochs.NextEpoch();

                if (allocated <= _epoch)
                {
                    // The allocator handed back a token we have already seen or superseded, which
                    // means it is not the authority it claims to be. Refusing is the only safe
                    // response: promoting on a non-unique token is promoting without fencing.
                    return new PromotionResult(PromotionOutcome.Fenced, _epoch, ReplicatedSequence);
                }

                _epoch = allocated;
                Role = NodeRole.Primary;
                _leaseHolder = NodeId;
                _leaseExpiresAt = _clock() + (long)_leaseDuration.TotalMilliseconds;
                Promotions++;

                // Publishing resumes at the next unused sequence, never below what was replicated.
                return new PromotionResult(PromotionOutcome.Promoted, _epoch, ReplicatedSequence + 1);
            }
        }

        /// <summary>
        /// Whether a packet stamped with <paramref name="epoch"/> may be accepted.
        /// </summary>
        /// <remarks>
        /// The receiving half of fencing. A subscriber that has seen epoch N must reject anything
        /// from an earlier epoch, or a resurrected primary's in-flight packets would be applied
        /// after the new primary's and silently corrupt the book.
        /// </remarks>
        public bool AcceptsEpoch(ulong epoch)
        {
            lock (_gate)
            {
                if (epoch < _epoch)
                    return false;

                if (epoch > _epoch)
                {
                    _epoch = epoch;

                    if (Role == NodeRole.Primary)
                    {
                        Role = NodeRole.Backup;
                        FencedAttempts++;
                        Fenced?.Invoke(epoch);
                    }
                }

                return true;
            }
        }

        /// <summary>Withdraws this node permanently.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                Role = NodeRole.Stopped;
                _leaseHolder = _leaseHolder == NodeId ? null : _leaseHolder;
            }
        }
    }
}
