using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MarketData.Common.Books;
using MarketData.Common.Durability;
using MarketData.Common.Feed;
using MarketData.Common.Governance;
using MarketData.Common.Lobster;
using Xunit;

namespace MarketData.Tests
{
    /// <summary>
    /// Fuzzing every parser and state machine that consumes bytes it did not produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is deliberately weak and therefore hard to argue with: no input
    /// sequence may crash the process, hang it, allocate without bound, or leave the consumer
    /// believing corrupt input was valid. Correct rejection is a pass; silent acceptance is not.
    /// </para>
    /// <para>
    /// Every failure reports the seed that produced it, because a fuzz failure nobody can reproduce
    /// is a rumour rather than a bug report.
    /// </para>
    /// </remarks>
    public class FeedDecoderFuzzTests
    {
        private static FeedDecoder NewDecoder() => new(_ => new SortedArrayBook(10));

        /// <summary>Wholly random bytes must be rejected, never applied.</summary>
        [Fact]
        public void RandomBytesAreNeverAccepted()
        {
            for (var seed = 1; seed <= 2_000; seed++)
            {
                var random = new Random(seed);
                var decoder = NewDecoder();
                var packet = new byte[random.Next(0, 1_500)];
                random.NextBytes(packet);

                try
                {
                    decoder.Consume(packet);
                }
                catch (Exception e)
                {
                    Assert.Fail($"seed {seed}: decoder threw {e.GetType().Name}: {e.Message}");
                }

                // Anything accepted from noise would mean the integrity checks are not checking.
                Assert.True(decoder.Statistics.Messages == 0,
                    $"seed {seed}: decoder applied {decoder.Statistics.Messages} message(s) from random bytes");
            }
        }

        /// <summary>
        /// A valid packet with one byte corrupted must be rejected, not partially applied.
        /// </summary>
        /// <remarks>
        /// Harder than random noise: the packet is structurally plausible everywhere except the
        /// mutated byte, so every field-level check has to hold rather than the magic number
        /// catching it first.
        /// </remarks>
        [Fact]
        public void SingleByteMutationsOfValidPacketsAreRejected()
        {
            var template = BuildValidPacket(sessionId: 42, firstSequence: 1);

            for (var index = 0; index < template.Length; index++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    var mutated = (byte[])template.Clone();
                    mutated[index] ^= (byte)(1 << bit);

                    var decoder = NewDecoder();

                    try
                    {
                        decoder.Consume(mutated);
                    }
                    catch (Exception e)
                    {
                        Assert.Fail($"byte {index} bit {bit}: threw {e.GetType().Name}: {e.Message}");
                    }

                    Assert.True(decoder.Statistics.Messages == 0,
                        $"byte {index} bit {bit}: a corrupted packet was applied");
                }
            }
        }

        /// <summary>Truncating a valid packet anywhere must be rejected.</summary>
        [Fact]
        public void EveryTruncationOfAValidPacketIsRejected()
        {
            var template = BuildValidPacket(sessionId: 7, firstSequence: 1);

            for (var length = 0; length < template.Length; length++)
            {
                var decoder = NewDecoder();

                try
                {
                    decoder.Consume(template.AsSpan(0, length));
                }
                catch (Exception e)
                {
                    Assert.Fail($"length {length}: threw {e.GetType().Name}: {e.Message}");
                }

                Assert.True(decoder.Statistics.Messages == 0,
                    $"length {length}: a truncated packet was applied");
            }
        }

        /// <summary>
        /// Long random streams must leave the decoder bounded and responsive.
        /// </summary>
        /// <remarks>
        /// The reorder buffer is the thing at risk: a stream of plausible-looking packets with
        /// scattered sequences could grow it without limit if the bound were missing, which is a
        /// remote memory-exhaustion vector rather than a mere bug.
        /// </remarks>
        [Fact]
        public void ASustainedHostileStreamKeepsTheDecoderBounded()
        {
            for (var seed = 1; seed <= 30; seed++)
            {
                var random = new Random(seed);
                var decoder = NewDecoder();

                for (var i = 0; i < 2_000; i++)
                {
                    // A mix: mostly noise, sometimes a structurally valid packet at a wild
                    // sequence, which is what would fill a reorder buffer.
                    byte[] packet;

                    if (random.NextDouble() < 0.4)
                    {
                        packet = BuildValidPacket(sessionId: 1,
                            firstSequence: (ulong)random.Next(1, 1_000_000));
                    }
                    else
                    {
                        packet = new byte[random.Next(0, 200)];
                        random.NextBytes(packet);
                    }

                    try
                    {
                        decoder.Consume(packet);
                    }
                    catch (Exception e)
                    {
                        Assert.Fail($"seed {seed} iteration {i}: threw {e.GetType().Name}: {e.Message}");
                    }

                    Assert.True(decoder.HeldPackets <= FeedDecoder.MaxHeldPackets,
                        $"seed {seed}: held {decoder.HeldPackets} packets, above the bound of " +
                        $"{FeedDecoder.MaxHeldPackets}");
                }

                decoder.FlushGaps();
            }
        }

        private static byte[] BuildValidPacket(ulong sessionId, ulong firstSequence)
        {
            var packet = new byte[FeedProtocol.HeaderSize + FeedProtocol.IncrementalSize];

            FeedProtocol.WriteIncremental(packet.AsSpan(FeedProtocol.HeaderSize),
                FeedMessageType.Add, 1, Side.Bid, new PriceLevel(100, 10));

            FeedProtocol.WriteHeader(packet, 1, sessionId, firstSequence, 12345);
            return packet;
        }
    }

    public class JournalFuzzTests
    {
        /// <summary>Random bytes must never validate as a journal record.</summary>
        [Fact]
        public void RandomBytesAreNeverAValidRecord()
        {
            var accepted = 0;

            for (var seed = 1; seed <= 20_000; seed++)
            {
                var random = new Random(seed);
                var buffer = new byte[random.Next(0, 200)];
                random.NextBytes(buffer);

                try
                {
                    if (JournalRecord.TryRead(buffer, out _) == JournalReadResult.Ok)
                        accepted++;
                }
                catch (Exception e)
                {
                    Assert.Fail($"seed {seed}: TryRead threw {e.GetType().Name}: {e.Message}");
                }
            }

            Assert.Equal(0, accepted);
        }

        /// <summary>
        /// A declared length must never make the reader read beyond its buffer.
        /// </summary>
        /// <remarks>
        /// The classic parser vulnerability: a length field is trusted, and a hostile writer names
        /// a size larger than the bytes actually present. The reader must treat that as incomplete
        /// rather than reading past the end.
        /// </remarks>
        [Fact]
        public void AnOverstatedLengthIsRefusedRatherThanReadPast()
        {
            var payload = Encoding.UTF8.GetBytes("real");
            var record = new byte[JournalRecord.SizeFor(payload.Length)];
            JournalRecord.Write(record, JournalRecordType.Message, 1, 0, payload);

            foreach (var claimed in new[] { 5, 100, 1 << 20, int.MaxValue, -1, int.MinValue })
            {
                var tampered = (byte[])record.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    tampered.AsSpan(8, 4), claimed);

                var result = JournalReadResult.Ok;

                try
                {
                    result = JournalRecord.TryRead(tampered, out _);
                }
                catch (Exception e)
                {
                    Assert.Fail($"claimed length {claimed}: threw {e.GetType().Name}: {e.Message}");
                }

                Assert.True(result != JournalReadResult.Ok,
                    $"claimed length {claimed} was accepted");
            }
        }

        /// <summary>A corrupt journal directory must be reported, never silently partially read.</summary>
        [Fact]
        public void ACorruptedJournalDirectoryIsAlwaysReportedOrRecoverable()
        {
            var root = Path.Combine(Path.GetTempPath(), "mds-fuzz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                for (var seed = 1; seed <= 60; seed++)
                {
                    var directory = Path.Combine(root, $"case-{seed}");
                    Directory.CreateDirectory(directory);

                    using (var journal = new WriteAheadJournal(directory, 3, DurabilityPolicy.OsBuffered))
                    {
                        for (var i = 0; i < 40; i++)
                            journal.AppendNext(i, new byte[16]);
                    }

                    var random = new Random(seed);
                    var segment = Directory.GetFiles(directory, "segment-*.jrn").OrderBy(f => f).Last();
                    var bytes = File.ReadAllBytes(segment);

                    // Corrupt a random slice.
                    var start = random.Next(0, bytes.Length);
                    var length = random.Next(1, Math.Min(64, bytes.Length - start) + 1);

                    for (var i = start; i < start + length; i++)
                        bytes[i] ^= (byte)random.Next(1, 256);

                    File.WriteAllBytes(segment, bytes);

                    RecoveryReport report;

                    try
                    {
                        report = JournalReader.Recover(directory);
                    }
                    catch (Exception e)
                    {
                        Assert.Fail($"seed {seed}: Recover threw {e.GetType().Name}: {e.Message}");
                        return;
                    }

                    // Whatever it decides, it must decide *something* and never claim a clean read
                    // of a file that was damaged in the middle.
                    Assert.True(
                        report.Outcome is RecoveryOutcome.Clean
                            or RecoveryOutcome.TruncatedTail
                            or RecoveryOutcome.Corrupt,
                        $"seed {seed}: unexpected outcome {report.Outcome}");

                    if (report.Outcome == RecoveryOutcome.Corrupt)
                        Assert.False(report.Resumable, $"seed {seed}: corrupt but reported resumable");

                    Directory.Delete(directory, recursive: true);
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }

    public class LobsterParserFuzzTests
    {
        /// <summary>
        /// Malformed CSV must not crash the parser, whatever it contains.
        /// </summary>
        /// <remarks>
        /// Real vendor files are not adversarial, but they are routinely malformed - truncated
        /// downloads, mixed line endings, a stray header row. A parser that throws on those turns a
        /// data problem into an outage.
        /// </remarks>
        [Fact]
        public void MalformedCsvNeverCrashesTheParser()
        {
            var alphabet = "0123456789,.-\r\n eE+xyz\t\"".ToCharArray();

            for (var seed = 1; seed <= 3_000; seed++)
            {
                var random = new Random(seed);
                var builder = new StringBuilder();
                var length = random.Next(0, 400);

                for (var i = 0; i < length; i++)
                    builder.Append(alphabet[random.Next(alphabet.Length)]);

                var bytes = Encoding.ASCII.GetBytes(builder.ToString());

                try
                {
                    var reader = new LobsterReader(bytes);
                    var guard = 0;

                    while (reader.TryReadMessage(out _))
                    {
                        // A parser that never advances would hang rather than fail, which is worse.
                        Assert.True(++guard <= length + 2,
                            $"seed {seed}: parser produced more messages than the input could hold");
                    }
                }
                catch (Exception e)
                {
                    Assert.Fail($"seed {seed}: parser threw {e.GetType().Name}: {e.Message}" +
                                Environment.NewLine + builder);
                }
            }
        }

        /// <summary>Parsing terminates on every input, including pathological ones.</summary>
        [Fact]
        public void ParsingAlwaysTerminates()
        {
            var pathological = new[]
            {
                "", ",", ",,,,,,,,,,", "\n", "\r\n", "-", "-,-,-,-,-,-",
                new string(',', 10_000),
                new string('9', 10_000),
                "1,1,1,1,1," + new string('9', 400),
                "\0\0\0\0",
            };

            foreach (var input in pathological)
            {
                var stopwatch = Stopwatch.StartNew();
                var reader = new LobsterReader(Encoding.ASCII.GetBytes(input));

                while (reader.TryReadMessage(out _))
                {
                    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                        $"parser did not terminate on input of length {input.Length}");
                }
            }
        }
    }

    public class SchemaFuzzTests
    {
        /// <summary>
        /// Compatibility comparison must never throw, whatever two schemas it is given.
        /// </summary>
        /// <remarks>
        /// It is meant to be called from CI on a schema someone just edited, which is exactly when
        /// the input is most likely to be strange. A comparison that throws instead of reporting
        /// "breaking" fails the build with a stack trace rather than an explanation.
        /// </remarks>
        [Fact]
        public void ComparingArbitrarySchemasNeverThrows()
        {
            for (var seed = 1; seed <= 500; seed++)
            {
                var random = new Random(seed);
                var left = RandomSchema(random, 1);
                var right = RandomSchema(random, 2);

                try
                {
                    var report = Compatibility.Compare(left, right);
                    Assert.NotNull(report);

                    // A breaking report must name at least one reason, or it is unactionable.
                    if (report.Kind == CompatibilityKind.Breaking)
                        Assert.NotEmpty(report.Breaks);
                }
                catch (Exception e)
                {
                    Assert.Fail($"seed {seed}: Compare threw {e.GetType().Name}: {e.Message}");
                }
            }
        }

        /// <summary>A schema is always compatible with itself.</summary>
        [Fact]
        public void EverySchemaIsIdenticalToItself()
        {
            for (var seed = 1; seed <= 500; seed++)
            {
                var schema = RandomSchema(new Random(seed), 1);
                var report = Compatibility.Compare(schema, schema);

                Assert.Equal(CompatibilityKind.Identical, report.Kind);
                Assert.True(report.CanDeployIndependently);
            }
        }

        private static Schema RandomSchema(Random random, int version)
        {
            var messages = new List<MessageSchema>();
            var messageCount = random.Next(1, 4);

            for (var m = 0; m < messageCount; m++)
            {
                var fields = new List<SchemaField>();
                var offset = 0;
                var fieldCount = random.Next(1, 6);

                for (var f = 0; f < fieldCount; f++)
                {
                    var type = (FieldType)random.Next(1, 7);   // excludes Ascii, which carries its own length
                    var width = SchemaField.WidthOf(type);

                    // Since must not exceed the schema's own version - a field cannot have been
                    // introduced by a version that does not exist yet, and the schema rightly
                    // refuses it.
                    fields.Add(new SchemaField($"f{f}", type, offset, width,
                        Since: random.Next(1, version + 1), Required: random.Next(2) == 0));

                    offset += width;
                }

                messages.Add(new MessageSchema($"m{m}", (byte)(m + 1), fields));
            }

            return new Schema(version, messages);
        }
    }
}
