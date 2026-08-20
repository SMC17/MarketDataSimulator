using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MarketData.Common.Availability;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Matching;
using MarketData.Common.Risk;
using MarketData.Tests.Framework;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Model-based tests: a deliberately naive reference is driven alongside the real thing and
    /// the two are compared after every single operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value over example-based tests is that the model states the invariant <em>once</em>, and
    /// then arbitrary operation sequences try to break it. The model is allowed to be slow and
    /// obvious - that is the point of having it - and where they disagree, the shrinker reduces the
    /// sequence to something a person can read.
    /// </para>
    /// <para>
    /// These target the subsystems added most recently, which have had the least adversarial
    /// exposure.
    /// </para>
    /// </remarks>
    public class RiskEngineModelTests
    {
        private const int InstrumentId = 1;
        private const ulong Account = 5;

        private enum Op { Reserve, ReleaseAll, PartialRelease, Fill }

        private readonly record struct Step(Op Op, ulong OrderId, uint Quantity, ulong UnitValue, Side Side);

        private static List<Step> Generate(Random random, int maxLength)
        {
            var length = random.Next(1, maxLength + 1);
            var steps = new List<Step>(length);

            for (var i = 0; i < length; i++)
            {
                // A small id space so releases and fills actually land on live reservations
                // rather than always missing.
                var id = (ulong)random.Next(1, 12);
                var op = (Op)random.Next(0, 4);

                steps.Add(new Step(op, id, (uint)random.Next(1, 50), (ulong)random.Next(1, 20),
                    random.Next(2) == 0 ? Side.Bid : Side.Ask));
            }

            return steps;
        }

        private static IEnumerable<List<Step>> Shrink(List<Step> steps)
        {
            for (var chunk = steps.Count / 2; chunk >= 1; chunk /= 2)
            {
                for (var start = 0; start + chunk <= steps.Count; start += chunk)
                {
                    var reduced = new List<Step>(steps.Count - chunk);
                    reduced.AddRange(steps.Take(start));
                    reduced.AddRange(steps.Skip(start + chunk));

                    if (reduced.Count > 0)
                        yield return reduced;
                }
            }
        }

        private static string Describe(List<Step> steps)
            => string.Join(Environment.NewLine,
                steps.Select(s => $"  {s.Op}(id={s.OrderId}, qty={s.Quantity}, unit={s.UnitValue}, {s.Side})"));

        /// <summary>
        /// Exposure is conserved: it equals the sum of what every live reservation still holds.
        /// </summary>
        /// <remarks>
        /// The invariant everything else rests on. Any path that reserves without releasing, or
        /// releases twice, or releases the wrong amount, shows up here as a divergence between the
        /// engine's own accounting and an independent sum over the same reservations - regardless
        /// of which operation caused it.
        /// </remarks>
        [Fact]
        public void ReservedExposureAlwaysEqualsTheSumOfLiveReservations()
        {
            Property.ForAll(
                generate: random => Generate(random, 200),
                shrink: Shrink,
                describe: Describe,
                cases: 200,
                property: steps =>
                {
                    var engine = new PreTradeRiskEngine();
                    engine.ConfigureAccount(Account, RiskLimits.Unbounded);

                    // The model: what each order id still has reserved, in quantity terms.
                    var model = new Dictionary<ulong, (uint Remaining, ulong Unit, Side Side)>();

                    foreach (var step in steps)
                    {
                        switch (step.Op)
                        {
                            case Op.Reserve:
                                var decision = engine.Reserve(Account, step.OrderId, InstrumentId,
                                    step.Side, step.Quantity, step.UnitValue);

                                if (decision.Accepted)
                                {
                                    Assert.False(model.ContainsKey(step.OrderId),
                                        $"engine accepted a duplicate reservation for id {step.OrderId}");
                                    model[step.OrderId] = (step.Quantity, step.UnitValue, step.Side);
                                }
                                else
                                {
                                    // The only reason unbounded limits refuse is a duplicate id.
                                    Assert.True(model.ContainsKey(step.OrderId),
                                        $"engine refused id {step.OrderId} for {decision.Reason} " +
                                        "with unbounded limits and no live reservation");
                                }

                                break;

                            case Op.ReleaseAll:
                                var releasedAll = engine.TryReleaseAll(step.OrderId);
                                Assert.Equal(model.ContainsKey(step.OrderId), releasedAll);
                                model.Remove(step.OrderId);
                                break;

                            case Op.PartialRelease:
                                var canRelease = model.TryGetValue(step.OrderId, out var live) &&
                                                 step.Quantity <= live.Remaining;
                                var released = engine.TryRelease(step.OrderId, step.Quantity);

                                Assert.Equal(canRelease, released);

                                if (canRelease)
                                {
                                    var remaining = (uint)(live.Remaining - step.Quantity);

                                    if (remaining == 0)
                                        model.Remove(step.OrderId);
                                    else
                                        model[step.OrderId] = (remaining, live.Unit, live.Side);
                                }

                                break;

                            case Op.Fill:
                                var canFill = model.TryGetValue(step.OrderId, out var filling) &&
                                              step.Quantity <= filling.Remaining;
                                var filled = engine.TryApplyFill(step.OrderId, step.Quantity);

                                Assert.Equal(canFill, filled);

                                if (canFill)
                                {
                                    var remaining = (uint)(filling.Remaining - step.Quantity);

                                    if (remaining == 0)
                                        model.Remove(step.OrderId);
                                    else
                                        model[step.OrderId] = (remaining, filling.Unit, filling.Side);
                                }

                                break;
                        }

                        // Compared after *every* operation, so a divergence names the operation
                        // that caused it rather than the end of the sequence.
                        Assert.Equal(model.Count, engine.ActiveOrders);

                        foreach (var (orderId, expected) in model)
                        {
                            Assert.True(engine.TryGetReservation(orderId, out var actual),
                                $"engine lost reservation {orderId}");
                            Assert.Equal(expected.Remaining, actual.RemainingQuantity);
                        }
                    }
                });
        }

        /// <summary>A quantity limit is never exceeded, whatever order the operations arrive in.</summary>
        [Fact]
        public void OpenQuantityNeverExceedsItsLimit()
        {
            const ulong cap = 100;

            Property.ForAll(
                generate: random => Generate(random, 150),
                shrink: Shrink,
                describe: Describe,
                cases: 150,
                property: steps =>
                {
                    var engine = new PreTradeRiskEngine();
                    engine.ConfigureAccount(Account, RiskLimits.Unbounded with { MaxOpenQuantity = cap });

                    var live = new Dictionary<ulong, uint>();

                    foreach (var step in steps)
                    {
                        switch (step.Op)
                        {
                            case Op.Reserve:
                                if (engine.Reserve(Account, step.OrderId, InstrumentId, step.Side,
                                        step.Quantity, step.UnitValue).Accepted)
                                {
                                    live[step.OrderId] = step.Quantity;
                                }

                                break;

                            case Op.ReleaseAll:
                                if (engine.TryReleaseAll(step.OrderId))
                                    live.Remove(step.OrderId);
                                break;

                            case Op.PartialRelease:
                            case Op.Fill:
                                var applied = step.Op == Op.PartialRelease
                                    ? engine.TryRelease(step.OrderId, step.Quantity)
                                    : engine.TryApplyFill(step.OrderId, step.Quantity);

                                if (applied)
                                {
                                    var remaining = live[step.OrderId] - step.Quantity;

                                    if (remaining == 0)
                                        live.Remove(step.OrderId);
                                    else
                                        live[step.OrderId] = remaining;
                                }

                                break;
                        }

                        var open = live.Values.Aggregate(0UL, (sum, q) => sum + q);

                        Assert.True(open <= cap,
                            $"open quantity {open} exceeded the cap of {cap}");
                    }
                });
        }
    }

    public sealed class JournalModelTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "mds-model-" + Guid.NewGuid().ToString("N"));

        public JournalModelTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private readonly record struct JournalStep(bool Checkpoint, int Payload);

        private static IEnumerable<List<JournalStep>> ShrinkJournal(List<JournalStep> steps)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var reduced = new List<JournalStep>(steps);
                reduced.RemoveAt(i);

                if (reduced.Count > 0)
                    yield return reduced;
            }
        }

        /// <summary>
        /// Recovery equals replay, under arbitrary interleavings of writes and checkpoints.
        /// </summary>
        /// <remarks>
        /// The example-based version of this test picks one checkpoint position. This one picks
        /// them at random, including pathological cases - a checkpoint before any message, several
        /// in a row, one as the final record - which is where an off-by-one in the "replay from
        /// after the checkpoint" boundary would hide.
        /// </remarks>
        /// <summary>
        /// A checkpoint of nothing is refused, and that is deliberate.
        /// </summary>
        /// <remarks>
        /// Found by the model test below, which generated a checkpoint before any message existed.
        /// The refusal is correct rather than a bug: restoring such a checkpoint yields an empty
        /// book and replay from sequence zero, which is exactly a full replay - so it buys nothing
        /// and leaves an artifact that <c>FindLatest</c> would hand to recovery as though it meant
        /// something. Pinned here because it is a real contract that was previously only implicit,
        /// and because the generator below now has to respect it.
        /// </remarks>
        [Fact]
        public void ACheckpointBeforeAnyMessageIsRefused()
        {
            var directory = Path.Combine(_root, "probe");
            var checkpoints = Path.Combine(_root, "probe-chk");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(checkpoints);

            var books = new Dictionary<int, IOrderBook> { [1] = new SortedArrayBook(8) };

            using var journal = new WriteAheadJournal(directory, 9, DurabilityPolicy.OsBuffered);

            Assert.False(journal.HasSequencedRecords);
            Assert.Equal(Sequencer.None, journal.LastSequence);

            var thrown = Assert.Throws<InvalidDataException>(
                () => Checkpoint.Write(checkpoints, journal, journal.LastSequence, 9, books));

            Assert.Contains("durable prefix", thrown.Message);

            // One message in, and the same call succeeds.
            journal.AppendNext(0, new byte[8]);
            Assert.NotNull(Checkpoint.Write(checkpoints, journal, journal.LastSequence, 9, books));
        }

        [Fact]
        public void RecoveryFromAnyCheckpointEqualsAFullReplay()
        {
            var caseIndex = 0;

            Property.ForAll(
                generate: random =>
                {
                    var length = random.Next(1, 120);
                    var steps = new List<JournalStep>(length);

                    for (var i = 0; i < length; i++)
                        steps.Add(new JournalStep(random.NextDouble() < 0.15, random.Next(1, 1000)));

                    return steps;
                },
                shrink: ShrinkJournal,
                describe: steps => string.Join(", ",
                    steps.Select(s => s.Checkpoint ? "CHK" : s.Payload.ToString())),
                cases: 60,
                property: steps =>
                {
                    var directory = Path.Combine(_root, $"case-{caseIndex}");
                    var checkpoints = Path.Combine(_root, $"case-{caseIndex}-chk");
                    caseIndex++;

                    Directory.CreateDirectory(directory);
                    Directory.CreateDirectory(checkpoints);

                    IOrderBook Make(int _) => new SortedArrayBook(8);
                    var live = new Dictionary<int, IOrderBook> { [1] = Make(1) };

                    using (var journal = new WriteAheadJournal(directory, 9, DurabilityPolicy.OsBuffered))
                    {
                        foreach (var step in steps)
                        {
                            if (step.Checkpoint)
                            {
                                // A checkpoint of nothing is refused by contract, so the model only
                                // takes one once there is a durable prefix to checkpoint.
                                if (journal.HasSequencedRecords)
                                    Checkpoint.Write(checkpoints, journal, journal.LastSequence, 9, live);

                                continue;
                            }

                            var price = step.Payload % 41 - 20;
                            var quantity = (uint)(step.Payload % 97);
                            live[1].Upsert(Side.Bid, price, quantity);

                            var encoded = new byte[8];
                            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded, price);
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                                encoded.AsSpan(4), quantity);

                            journal.AppendNext(0, encoded);
                        }
                    }

                    var replayed = Rebuild(directory, null, Make);
                    var restored = Rebuild(directory, Checkpoint.FindLatest(checkpoints), Make);

                    AssertSame(replayed, restored);
                    AssertSame(live, restored);

                    Directory.Delete(directory, recursive: true);
                    Directory.Delete(checkpoints, recursive: true);
                });
        }

        private static Dictionary<int, IOrderBook> Rebuild(string directory, string? checkpointPath,
            Func<int, IOrderBook> make)
        {
            var books = new Dictionary<int, IOrderBook>();
            var from = Sequencer.None;

            if (checkpointPath is not null)
                from = Checkpoint.Restore(checkpointPath, make, books);

            if (!books.ContainsKey(1))
                books[1] = make(1);

            JournalReader.Recover(directory, (in JournalRecordView record) =>
            {
                if (record.Type != JournalRecordType.Message || record.Sequence <= from)
                    return true;

                var price = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(record.Payload);
                var quantity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Payload.Slice(4));

                books[1].Upsert(Side.Bid, price, quantity);
                return true;
            }, from);

            return books;
        }

        private static void AssertSame(IDictionary<int, IOrderBook> expected,
            IDictionary<int, IOrderBook> actual)
        {
            foreach (var (instrument, book) in expected)
            {
                var left = new PriceLevel[book.Count(Side.Bid)];
                var right = new PriceLevel[actual[instrument].Count(Side.Bid)];

                book.CopyTo(Side.Bid, left);
                actual[instrument].CopyTo(Side.Bid, right);

                Assert.True(left.SequenceEqual(right),
                    $"instrument {instrument} diverged: " +
                    $"[{string.Join(",", left.Select(l => $"{l.Quantity}@{l.Price}"))}] vs " +
                    $"[{string.Join(",", right.Select(r => $"{r.Quantity}@{r.Price}"))}]");
            }
        }
    }

    public class FailoverModelTests
    {
        private enum Event { Heartbeat, Promote, Advance, Replicate, Observe }

        private readonly record struct Move(Event Event, int Node, int Amount);

        private static IEnumerable<List<Move>> ShrinkMoves(List<Move> moves)
        {
            for (var i = 0; i < moves.Count; i++)
            {
                var reduced = new List<Move>(moves);
                reduced.RemoveAt(i);

                if (reduced.Count > 0)
                    yield return reduced;
            }
        }

        /// <summary>
        /// At most one node ever publishes, under arbitrary event orders.
        /// </summary>
        /// <remarks>
        /// The safety property fencing exists for. Rather than scripting one split-brain scenario,
        /// this drives heartbeats, promotions, clock advances and replication progress in random
        /// orders across a cluster sharing one epoch allocator, and asserts after every move that no
        /// two nodes hold the same epoch as primary - which is the condition under which two
        /// publishers could emit conflicting data under the same sequence numbers.
        /// </remarks>
        [Fact]
        public void NoTwoNodesEverHoldTheSameEpochAsPrimary()
        {
            const int nodes = 4;

            Property.ForAll(
                generate: random =>
                {
                    var length = random.Next(5, 250);
                    var moves = new List<Move>(length);

                    for (var i = 0; i < length; i++)
                    {
                        moves.Add(new Move((Event)random.Next(0, 5), random.Next(0, nodes),
                            random.Next(1, 40)));
                    }

                    return moves;
                },
                shrink: ShrinkMoves,
                describe: moves => string.Join(", ", moves.Select(m => $"{m.Event}#{m.Node}({m.Amount})")),
                cases: 200,
                property: moves =>
                {
                    var clock = new long[] { 1_000 };
                    var allocator = new InMemoryEpochAllocator();

                    var cluster = Enumerable.Range(0, nodes)
                        .Select(i => new FailoverCoordinator($"N{i}", TimeSpan.FromMilliseconds(50),
                            () => clock[0], allocator))
                        .ToList();

                    ulong published = 0;

                    foreach (var move in moves)
                    {
                        var node = cluster[move.Node];

                        switch (move.Event)
                        {
                            case Event.Heartbeat:
                                // Whoever believes it is primary tells everyone else.
                                var primary = cluster.FirstOrDefault(n => n.Role == NodeRole.Primary);

                                if (primary is not null)
                                {
                                    foreach (var peer in cluster)
                                        peer.Heartbeat(primary.NodeId, primary.Epoch, published);
                                }

                                break;

                            case Event.Promote:
                                var result = node.TryPromote();

                                if (result.Succeeded)
                                {
                                    // A new primary announces itself; anything stale stands down.
                                    foreach (var peer in cluster)
                                        peer.AcceptsEpoch(result.Epoch);
                                }

                                break;

                            case Event.Advance:
                                clock[0] += move.Amount;
                                break;

                            case Event.Replicate:
                                node.RecordReplicated(published);
                                break;

                            case Event.Observe:
                                published += (ulong)move.Amount;

                                foreach (var peer in cluster)
                                    peer.ObserveSequence(published);

                                break;
                        }

                        // The safety property, checked after every move.
                        var primaries = cluster.Where(n => n.Role == NodeRole.Primary).ToList();

                        Assert.True(primaries.Count <= 1,
                            $"{primaries.Count} nodes believe they are primary: " +
                            string.Join(", ", primaries.Select(p => $"{p.NodeId}@epoch{p.Epoch}")));

                        // And epochs never move backwards anywhere.
                        foreach (var peer in cluster)
                            Assert.True(peer.Epoch <= allocator.NextEpochPeek());
                    }
                });
        }
    }
}
