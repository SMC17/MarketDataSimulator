using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MarketData.Common.Durability;

namespace MarketData.Common.Availability
{
    /// <summary>What a recovery from the replica would actually cost.</summary>
    /// <param name="RecoveryPointSequences">
    /// RPO, in records: how many the replica is behind, i.e. what a failover would lose.
    /// </param>
    /// <param name="RecoveryTime">RTO: measured time to rebuild usable state from the replica.</param>
    public sealed record RecoveryObjectives(
        ulong RecoveryPointSequences,
        TimeSpan RecoveryTime,
        ulong PrimarySequence,
        ulong ReplicaSequence,
        long BytesShipped)
    {
        public bool IsLossless => RecoveryPointSequences == 0;
    }

    /// <summary>
    /// Copies journal segments to a replica directory, and measures what that buys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shipping whole segments rather than streaming records is deliberate. A segment is
    /// self-describing and internally checksummed, so a partially copied one is detectable on
    /// arrival by the same reader that validates a local log - no separate verification path, and
    /// no way for a torn copy to be mistaken for a complete one.
    /// </para>
    /// <para>
    /// The consequence is that RPO is bounded by segment rotation, not by copy frequency: an
    /// unrotated active segment is not shipped, so everything in it is at risk. That is a real
    /// property of this design and it is measured rather than glossed - <see cref="Ship"/> reports
    /// how far behind the replica actually is, and the number is usually not zero.
    /// </para>
    /// <para>
    /// A disaster recovery plan whose RPO and RTO are asserted rather than measured is a plan whose
    /// first real test is the disaster.
    /// </para>
    /// </remarks>
    public sealed class JournalShipper
    {
        private readonly string _primaryDirectory;
        private readonly string _replicaDirectory;
        private readonly Dictionary<string, long> _shipped = new();

        public JournalShipper(string primaryDirectory, string replicaDirectory)
        {
            _primaryDirectory = primaryDirectory;
            _replicaDirectory = replicaDirectory;
            Directory.CreateDirectory(replicaDirectory);
        }

        public long BytesShipped { get; private set; }
        public long SegmentsShipped { get; private set; }
        public long SegmentsSkipped { get; private set; }

        /// <summary>
        /// Copies every sealed segment the replica does not already hold.
        /// </summary>
        /// <remarks>
        /// The newest segment is skipped while the journal is open, because it is still being
        /// appended to: copying it would ship a prefix whose tail changes underneath, and the copy
        /// would be a torn record rather than a complete one. Set
        /// <paramref name="includeActiveSegment"/> only when the primary is known to be stopped.
        /// </remarks>
        public RecoveryObjectives Ship(bool includeActiveSegment = false)
        {
            var segments = Directory.Exists(_primaryDirectory)
                ? Directory.GetFiles(_primaryDirectory, "segment-*.jrn").OrderBy(f => f).ToList()
                : new List<string>();

            var shippable = includeActiveSegment || segments.Count == 0
                ? segments
                : segments.Take(segments.Count - 1).ToList();

            foreach (var source in shippable)
            {
                var name = Path.GetFileName(source);
                var length = new FileInfo(source).Length;

                // A segment already shipped at its final length never changes again, so re-copying
                // it would be pure cost.
                if (_shipped.TryGetValue(name, out var previous) && previous == length)
                {
                    SegmentsSkipped++;
                    continue;
                }

                var destination = Path.Combine(_replicaDirectory, name);
                File.Copy(source, destination, overwrite: true);

                _shipped[name] = length;
                BytesShipped += length;
                SegmentsShipped++;
            }

            return Measure();
        }

        /// <summary>
        /// Measures RPO and RTO against the current replica contents.
        /// </summary>
        /// <remarks>
        /// RTO is timed by actually replaying the replica, not estimated. An estimate cannot
        /// discover that the replica is corrupt, and discovering that during a disaster is the
        /// scenario the whole exercise exists to avoid.
        /// </remarks>
        public RecoveryObjectives Measure()
        {
            var primary = JournalReader.Recover(_primaryDirectory);

            var stopwatch = Stopwatch.StartNew();
            var replica = JournalReader.Recover(_replicaDirectory);
            stopwatch.Stop();

            var lost = primary.LastSequence >= replica.LastSequence
                ? primary.LastSequence - replica.LastSequence
                : 0;

            return new RecoveryObjectives(lost, stopwatch.Elapsed,
                primary.LastSequence, replica.LastSequence, BytesShipped);
        }

        /// <summary>
        /// Confirms the replica is readable and internally consistent.
        /// </summary>
        /// <remarks>
        /// A replica nobody has ever read is a guess. This is the drill that turns it into a fact,
        /// and it is meant to be run on a schedule rather than at the moment of need.
        /// </remarks>
        public RecoveryReport Verify() => JournalReader.Recover(_replicaDirectory);
    }
}
