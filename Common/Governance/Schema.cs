using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarketData.Common.Governance
{
    /// <summary>Wire types a schema field may take. Width is fixed by the type.</summary>
    public enum FieldType : byte
    {
        UInt8 = 1,
        UInt16 = 2,
        UInt32 = 3,
        UInt64 = 4,
        Int32 = 5,
        Int64 = 6,

        /// <summary>Fixed-length ASCII, padded. Length is carried separately.</summary>
        Ascii = 7,
    }

    /// <summary>One field in a versioned message layout.</summary>
    /// <param name="Name">Stable identity. Renaming a field is a breaking change; see below.</param>
    /// <param name="Type">Wire type.</param>
    /// <param name="Offset">Byte offset from the start of the message body.</param>
    /// <param name="Length">Bytes occupied. Fixed by <paramref name="Type"/> except for ASCII.</param>
    /// <param name="Since">Schema version that introduced the field.</param>
    /// <param name="Required">
    /// Whether a reader must understand the field. A required field added to an existing message is
    /// a breaking change; an optional one is not.
    /// </param>
    public sealed record SchemaField(
        string Name,
        FieldType Type,
        int Offset,
        int Length,
        int Since = 1,
        bool Required = true)
    {
        public static int WidthOf(FieldType type) => type switch
        {
            FieldType.UInt8 => 1,
            FieldType.UInt16 => 2,
            FieldType.UInt32 => 4,
            FieldType.UInt64 => 8,
            FieldType.Int32 => 4,
            FieldType.Int64 => 8,
            FieldType.Ascii => throw new ArgumentException("ASCII fields carry an explicit length.", nameof(type)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        public int End => Offset + Length;
    }

    /// <summary>The layout of one message type at one schema version.</summary>
    public sealed record MessageSchema(string Name, byte TypeCode, IReadOnlyList<SchemaField> Fields)
    {
        /// <summary>Bytes the message occupies, taken from the furthest field.</summary>
        public int Size => Fields.Count == 0 ? 0 : Fields.Max(field => field.End);

        /// <summary>Fields a reader at <paramref name="version"/> is expected to know.</summary>
        public IEnumerable<SchemaField> FieldsAsOf(int version)
            => Fields.Where(field => field.Since <= version);
    }

    /// <summary>
    /// A complete, versioned wire contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of writing the layout down as data rather than leaving it implicit in encoder and
    /// decoder source is that compatibility becomes a property that can be <em>checked</em> instead
    /// of a claim someone makes in a pull request. Two schema versions can be diffed mechanically,
    /// and the rules below decide whether the change is safe.
    /// </para>
    /// <para>
    /// The identity carried on the wire is <see cref="Fingerprint"/>, not the version number.
    /// Version numbers are administrative and can be bumped without changing anything, or - far
    /// worse - changed without being bumped. A fingerprint over the actual layout cannot be wrong
    /// about what the sender is sending.
    /// </para>
    /// </remarks>
    public sealed class Schema
    {
        public Schema(int version, IReadOnlyList<MessageSchema> messages)
        {
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version), version, "Versions start at 1.");

            Version = version;
            Messages = messages;

            var duplicateCodes = messages.GroupBy(m => m.TypeCode).FirstOrDefault(g => g.Count() > 1);
            if (duplicateCodes is not null)
                throw new ArgumentException($"Type code {duplicateCodes.Key} is used by more than one message.",
                    nameof(messages));

            foreach (var message in messages)
                Validate(message);

            Fingerprint = ComputeFingerprint(messages);
        }

        public int Version { get; }
        public IReadOnlyList<MessageSchema> Messages { get; }

        /// <summary>
        /// A stable hash of the layout itself, carried on the wire and compared at session start.
        /// </summary>
        public ulong Fingerprint { get; }

        public MessageSchema Find(byte typeCode)
            => Messages.FirstOrDefault(message => message.TypeCode == typeCode);

        /// <summary>
        /// Rejects layouts that are internally impossible, before anything encodes against them.
        /// </summary>
        /// <remarks>
        /// Overlapping fields are the interesting case. They are trivially easy to introduce by
        /// hand-editing an offset and essentially undetectable afterwards: the encoder writes both
        /// fields, the second silently truncates the first, and the corruption looks like bad data
        /// rather than a bad schema.
        /// </remarks>
        private static void Validate(MessageSchema message)
        {
            var ordered = message.Fields.OrderBy(field => field.Offset).ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var field = ordered[i];

                if (field.Offset < 0)
                    throw new ArgumentException($"{message.Name}.{field.Name} has a negative offset.");

                if (field.Length <= 0)
                    throw new ArgumentException($"{message.Name}.{field.Name} has a non-positive length.");

                if (field.Type != FieldType.Ascii && field.Length != SchemaField.WidthOf(field.Type))
                    throw new ArgumentException(
                        $"{message.Name}.{field.Name} is {field.Type} but declares {field.Length} bytes.");

                if (i > 0 && ordered[i - 1].End > field.Offset)
                    throw new ArgumentException(
                        $"{message.Name}.{field.Name} at {field.Offset} overlaps " +
                        $"{ordered[i - 1].Name} which ends at {ordered[i - 1].End}.");
            }

            if (message.Fields.Select(field => field.Name).Distinct().Count() != message.Fields.Count)
                throw new ArgumentException($"{message.Name} has duplicate field names.");
        }

        /// <summary>
        /// FNV-1a over the layout. Deterministic across runs and processes, which a managed
        /// string hash is explicitly not.
        /// </summary>
        private static ulong ComputeFingerprint(IReadOnlyList<MessageSchema> messages)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;

            void Mix(string text)
            {
                foreach (var b in Encoding.UTF8.GetBytes(text))
                {
                    hash ^= b;
                    hash *= prime;
                }
            }

            // Ordered so the fingerprint depends on the layout and not on declaration order.
            foreach (var message in messages.OrderBy(m => m.TypeCode))
            {
                Mix(message.Name);
                Mix(message.TypeCode.ToString());

                foreach (var field in message.Fields.OrderBy(f => f.Offset))
                {
                    Mix(field.Name);
                    Mix(((byte)field.Type).ToString());
                    Mix(field.Offset.ToString());
                    Mix(field.Length.ToString());
                    Mix(field.Required ? "R" : "O");
                }
            }

            return hash;
        }
    }
}
