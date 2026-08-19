using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Availability;
using MarketData.Common.Durability;
using Xunit;

namespace MarketData.Tests
{
    public class FailoverTests
    {
        /// <summary>
        /// A cluster sharing one epoch allocator, which is the only configuration in which
        /// fencing means anything. The incumbent's epochs are drawn from the same allocator, so a
        /// heartbeat claiming epoch N corresponds to a token that was genuinely issued.
        /// </summary>
        private sealed class Cluster
        {
            public Cluster(long startMs = 1_000) => Clock = new[] { startMs };

            public long[] Clock { get; }
            public InMemoryEpochAllocator Allocator { get; } = new();

            public FailoverCoordinator Node(string id, int leaseMs = 100)
                => new(id, TimeSpan.FromMilliseconds(leaseMs), () => Clock[0], Allocator);

            /// <summary>Issues the epoch an existing primary would already be holding.</summary>
            public ulong IncumbentEpoch() => Allocator.NextEpoch();
        }

        [Fact]
        public void ABackupWillNotPromoteWhileTheLeaseIsLive()
        {
            var cluster = new Cluster();
            var backup = cluster.Node("B");

            backup.Heartbeat("A", cluster.IncumbentEpoch(), sequence: 10);
            backup.RecordReplicated(10);

            Assert.False(backup.LeaseExpired);
            Assert.Equal(PromotionOutcome.IncumbentStillLive, backup.TryPromote().Outcome);
            Assert.Equal(NodeRole.Backup, backup.Role);
        }

        [Fact]
        public void ABackupPromotesOnceTheLeaseExpires()
        {
            var cluster = new Cluster();
            var backup = cluster.Node("B");

            backup.Heartbeat("A", cluster.IncumbentEpoch(), sequence: 10);
            backup.RecordReplicated(10);

            cluster.Clock[0] += 500;   // past the lease

            Assert.True(backup.LeaseExpired);

            var result = backup.TryPromote();

            Assert.True(result.Succeeded);
            Assert.Equal(NodeRole.Primary, backup.Role);
            Assert.Equal(2UL, result.Epoch);          // epoch advanced
            Assert.Equal(11UL, result.FromSequence);  // resumes after what it holds
        }

        /// <summary>
        /// A backup that is behind must not take over, or it reissues sequences.
        /// </summary>
        /// <remarks>
        /// Promoting while behind restarts publishing below what subscribers have already applied.
        /// They cannot detect it - the numbers look continuous - so every book downstream diverges
        /// silently.
        /// </remarks>
        [Fact]
        public void ABackupThatIsBehindIsRefused()
        {
            var cluster = new Cluster();
            var backup = cluster.Node("B");

            backup.Heartbeat("A", cluster.IncumbentEpoch(), sequence: 100);
            backup.RecordReplicated(97);   // three records behind

            cluster.Clock[0] += 500;

            Assert.Equal(PromotionOutcome.NotCaughtUp, backup.TryPromote().Outcome);
            Assert.Equal(NodeRole.Backup, backup.Role);

            backup.RecordReplicated(100);
            Assert.True(backup.TryPromote().Succeeded);
        }

        /// <summary>
        /// The split-brain case: a slow primary comes back and must stand down.
        /// </summary>
        /// <remarks>
        /// The whole reason for the epoch. A timeout cannot distinguish a dead peer from an
        /// unreachable one, so the resurrected primary genuinely believes it still holds the role.
        /// What stops it is learning of a higher epoch, at which point it must stop publishing
        /// immediately rather than argue.
        /// </remarks>
        [Fact]
        public void AResurrectedPrimaryIsFencedByTheHigherEpoch()
        {
            var cluster = new Cluster();

            var oldPrimary = cluster.Node("A");
            oldPrimary.RecordReplicated(50);
            Assert.True(oldPrimary.TryPromote().Succeeded);
            Assert.Equal(NodeRole.Primary, oldPrimary.Role);
            Assert.Equal(1UL, oldPrimary.Epoch);

            // Meanwhile the backup times it out and promotes to the next epoch.
            var newPrimary = cluster.Node("B");
            newPrimary.Heartbeat("A", epoch: 1, sequence: 50);
            newPrimary.RecordReplicated(50);
            cluster.Clock[0] += 500;
            var promotion = newPrimary.TryPromote();
            Assert.True(promotion.Succeeded);
            Assert.Equal(2UL, promotion.Epoch);

            // The old primary wakes and hears the newer epoch.
            var fencedAt = 0UL;
            oldPrimary.Fenced += epoch => fencedAt = epoch;
            oldPrimary.Heartbeat("B", promotion.Epoch, sequence: 60);

            Assert.Equal(NodeRole.Backup, oldPrimary.Role);
            Assert.Equal(2UL, fencedAt);
            Assert.Equal(1, oldPrimary.FencedAttempts);
        }

        /// <summary>A subscriber must reject packets from a superseded epoch.</summary>
        [Fact]
        public void PacketsFromASupersededEpochAreRejected()
        {
            var subscriber = new Cluster().Node("S");

            Assert.True(subscriber.AcceptsEpoch(1));
            Assert.True(subscriber.AcceptsEpoch(2));   // moves forward

            // In-flight packets from the old primary arrive late and must not be applied.
            Assert.False(subscriber.AcceptsEpoch(1));
            Assert.True(subscriber.AcceptsEpoch(2));
        }

        [Fact]
        public void OnlyOneNodeHoldsTheRoleAtEachEpoch()
        {
            // One allocator shared by every candidate. Without it each node would mint its own
            // "epoch 1" and fencing would be comparing equal tokens, which is no fencing at all.
            var cluster = new Cluster();
            var nodes = Enumerable.Range(0, 5).Select(i => cluster.Node($"N{i}")).ToList();

            foreach (var node in nodes)
                node.RecordReplicated(10);

            // Everyone races to promote off the same expired lease.
            var promoted = nodes.Select(node => (node, result: node.TryPromote()))
                .Where(entry => entry.result.Succeeded)
                .ToList();

            // Each takes a distinct epoch, and only the highest survives contact with the others.
            var epochs = promoted.Select(entry => entry.result.Epoch).ToList();
            Assert.Equal(epochs.Count, epochs.Distinct().Count());

            var highest = epochs.Max();

            foreach (var (node, result) in promoted)
            {
                node.AcceptsEpoch(highest);

                if (result.Epoch < highest)
                    Assert.Equal(NodeRole.Backup, node.Role);
            }

            Assert.Single(promoted.Where(entry => entry.node.Role == NodeRole.Primary));
        }

        [Fact]
        public void AStoppedNodeNeverPromotes()
        {
            var node = new Cluster().Node("A");
            node.RecordReplicated(5);
            node.Stop();

            Assert.Equal(PromotionOutcome.Fenced, node.TryPromote().Outcome);
            Assert.Equal(NodeRole.Stopped, node.Role);
        }
    }

    public class TelemetryTests
    {
        [Fact]
        public void QuantilesAreReportedAsUpperBounds()
        {
            var histogram = new LatencyHistogram();

            for (var i = 0; i < 1_000; i++)
                histogram.Record(100);

            histogram.Record(1_000_000);

            Assert.Equal(1_001, histogram.Count);

            // The bulk sits in the bucket whose top edge is 128.
            Assert.Equal(128, histogram.QuantileUpperBound(0.5));

            // The outlier is inside the top bound.
            Assert.True(histogram.QuantileUpperBound(1.0) >= 1_000_000);
            Assert.Equal(1_000_000, histogram.Max);
        }

        [Fact]
        public void AnEmptyHistogramReportsZeroRatherThanThrowing()
        {
            var histogram = new LatencyHistogram();

            Assert.Equal(0, histogram.Count);
            Assert.Equal(0, histogram.QuantileUpperBound(0.99));
            Assert.Equal(0, histogram.Mean);
        }

        [Fact]
        public void RecordingIsSafeFromManyThreads()
        {
            var histogram = new LatencyHistogram();
            const int threads = 8;
            const int each = 10_000;

            Parallel.For(0, threads, _ =>
            {
                for (var i = 0; i < each; i++)
                    histogram.Record(i % 1_000 + 1);
            });

            Assert.Equal(threads * each, histogram.Count);
        }

        [Fact]
        public void CountersAggregateAcrossThreads()
        {
            var telemetry = new Telemetry();

            Parallel.For(0, 8, _ =>
            {
                for (var i = 0; i < 1_000; i++)
                    telemetry.Increment("published");
            });

            Assert.Equal(8_000, telemetry.Counter("published"));
            Assert.Equal(0, telemetry.Counter("never-touched"));
        }

        /// <summary>
        /// Burn rate, not "are we meeting it", is the number worth alerting on.
        /// </summary>
        [Fact]
        public void BurnRateDistinguishesSurvivableFromNot()
        {
            var slo = new ServiceLevelObjective("delivery", 0.999, TimeSpan.FromHours(1));

            // 10% of an hour gone, 10% of the budget spent: exactly sustainable.
            var steady = slo.BurnRate(total: 100_000, failures: 10, elapsed: TimeSpan.FromMinutes(6));
            Assert.Equal(1.0, steady, 3);

            // Same failures, a tenth of the time: ten times too fast.
            var fast = slo.BurnRate(total: 100_000, failures: 100, elapsed: TimeSpan.FromMinutes(6));
            Assert.True(fast > 9 && fast < 11, $"expected roughly 10x, got {fast}");

            // Exactly 100 failures against a budget of 100 is the objective met precisely - the
            // budget is spent, not overspent. What makes it alarming is the burn rate: it was
            // spent in a tenth of the window.
            var atBudget = new SloStatus(slo, 100_000, 100, slo.BudgetConsumed(100_000, 100), fast);
            Assert.True(atBudget.Met);
            Assert.True(atBudget.WillBreach);

            // One more failure and it is genuinely missed.
            var over = new SloStatus(slo, 100_000, 101, slo.BudgetConsumed(100_000, 101), fast);
            Assert.False(over.Met);
        }

        [Fact]
        public void AnObjectiveInsideBudgetIsMet()
        {
            var slo = new ServiceLevelObjective("delivery", 0.99, TimeSpan.FromHours(1));

            Assert.Equal(1_000, slo.AllowedFailures(100_000), 6);
            Assert.Equal(0.5, slo.BudgetConsumed(100_000, 500), 6);

            var status = new SloStatus(slo, 100_000, 500, 0.5, 0.5);
            Assert.True(status.Met);
            Assert.False(status.WillBreach);
        }
    }

    /// <summary>
    /// Fault drills: break things on purpose and assert the invariants survive.
    /// </summary>
    /// <remarks>
    /// Network-level faults - loss, duplication, corruption, reordering - are covered by the
    /// deterministic datagram link and its own tests. These cover the faults that live above it:
    /// a primary dying mid-write, a replica that has fallen behind, and storage that has been
    /// damaged underneath a running system.
    /// </remarks>
    public sealed class ChaosDrillTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "mds-chaos-" + Guid.NewGuid().ToString("N"));

        public ChaosDrillTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string Dir(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Killing the primary mid-write must lose only what was never acknowledged.
        /// </summary>
        [Fact]
        public void DrillTheJournalSurvivesTheWriterDyingMidRecord()
        {
            var directory = Dir("kill-primary");
            const int written = 500;

            using (var journal = new WriteAheadJournal(directory, 1, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 0; i < written; i++)
                    journal.AppendNext(i, new byte[] { (byte)(i & 0xFF) });
            }

            // The kill: truncate the final record part-way through.
            var segment = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(f => f).Last();
            var bytes = File.ReadAllBytes(segment);
            File.WriteAllBytes(segment, bytes.AsSpan(0, bytes.Length - 8).ToArray());

            var report = JournalReader.Recover(directory);

            Assert.True(report.Resumable, "a torn tail must be recoverable");
            Assert.Equal(RecoveryOutcome.TruncatedTail, report.Outcome);

            // Everything before the torn record survived.
            Assert.True(report.LastSequence >= (ulong)(written - 1),
                $"lost more than the torn record: last sequence {report.LastSequence} of {written}");
        }

        /// <summary>
        /// Damage in the middle of the log must be refused, not silently skipped.
        /// </summary>
        [Fact]
        public void DrillCorruptStorageIsRefusedRatherThanPartiallyTrusted()
        {
            var directory = Dir("corrupt-storage");

            using (var journal = new WriteAheadJournal(directory, 1, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 0; i < 200; i++)
                    journal.AppendNext(i, new byte[64]);
            }

            var segment = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(f => f).Last();
            var bytes = File.ReadAllBytes(segment);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(segment, bytes);

            var report = JournalReader.Recover(directory);

            Assert.Equal(RecoveryOutcome.Corrupt, report.Outcome);
            Assert.False(report.Resumable);
        }

        /// <summary>
        /// A DR drill: ship, then rebuild from the replica and measure what it cost.
        /// </summary>
        [Fact]
        public void DrillDisasterRecoveryMeasuresItsOwnRpoAndRto()
        {
            var primary = Dir("dr-primary");
            var replica = Dir("dr-replica");

            var segmentBytes = JournalRecord.SizeFor(16) + JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            var payload = new byte[4096];

            using (var journal = new WriteAheadJournal(primary, 1, DurabilityPolicy.OsBuffered, segmentBytes))
            {
                for (var i = 0; i < 800; i++)
                    journal.AppendNext(i, payload);
            }

            var shipper = new JournalShipper(primary, replica);

            // With the primary stopped the active segment is safe to ship, so this is lossless.
            var objectives = shipper.Ship(includeActiveSegment: true);

            Assert.True(shipper.SegmentsShipped > 0, "nothing was shipped");
            Assert.Equal(objectives.PrimarySequence, objectives.ReplicaSequence);
            Assert.True(objectives.IsLossless);
            Assert.Equal(0UL, objectives.RecoveryPointSequences);
            Assert.True(objectives.RecoveryTime > TimeSpan.Zero, "RTO must be measured, not assumed");

            Assert.Equal(RecoveryOutcome.Clean, shipper.Verify().Outcome);
        }

        /// <summary>
        /// Shipping while the primary is live leaves the active segment behind, and that loss is
        /// reported rather than hidden.
        /// </summary>
        [Fact]
        public void DrillShippingALiveJournalReportsANonZeroRecoveryPoint()
        {
            var primary = Dir("dr-live-primary");
            var replica = Dir("dr-live-replica");

            var segmentBytes = JournalRecord.SizeFor(16) + JournalRecord.SizeFor(JournalRecord.MaxPayloadSize);
            var payload = new byte[4096];

            using var journal = new WriteAheadJournal(primary, 1, DurabilityPolicy.OsBuffered, segmentBytes);

            for (var i = 0; i < 800; i++)
                journal.AppendNext(i, payload);

            journal.Sync();

            var shipper = new JournalShipper(primary, replica);
            var objectives = shipper.Ship(includeActiveSegment: false);

            // The active segment was not shipped, so the replica is behind - and says so.
            Assert.False(objectives.IsLossless);
            Assert.True(objectives.RecoveryPointSequences > 0,
                "shipping a live journal must report the records still at risk");
            Assert.True(objectives.ReplicaSequence < objectives.PrimarySequence);
        }

        /// <summary>
        /// After a failover, the surviving node must resume above every sequence anyone applied.
        /// </summary>
        [Fact]
        public void DrillFailoverNeverReissuesASequence()
        {
            var primary = Dir("failover-primary");
            var clock = new long[] { 1_000 };

            ulong lastPublished;

            using (var journal = new WriteAheadJournal(primary, 1, DurabilityPolicy.SyncEachRecord))
            {
                for (var i = 0; i < 100; i++)
                    journal.AppendNext(i, new byte[16]);

                lastPublished = journal.LastSequence;
            }

            // The backup has replicated everything the primary durably wrote. The allocator is
            // shared with the incumbent, so the epoch it heard was genuinely issued.
            var allocator = new InMemoryEpochAllocator();
            var incumbentEpoch = allocator.NextEpoch();
            var backup = new FailoverCoordinator("B", TimeSpan.FromMilliseconds(50),
                () => clock[0], allocator);
            backup.Heartbeat("A", incumbentEpoch, sequence: lastPublished);

            var recovered = JournalReader.Recover(primary);
            backup.RecordReplicated(recovered.LastSequence);

            clock[0] += 500;
            var promotion = backup.TryPromote();

            Assert.True(promotion.Succeeded);
            Assert.True(promotion.FromSequence > lastPublished,
                $"resumed at {promotion.FromSequence}, which reissues sequences up to {lastPublished}");
        }
    }
}
