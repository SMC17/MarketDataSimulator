using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
    /// <param name="Name">Stable field identity.</param>
    /// <param name="Type">Wire type.</param>
    /// <param name="Offset">Byte offset from the start of the message body.</param>
    /// <param name="Length">Bytes occupied. Fixed by <paramref name="Type"/> except for ASCII.</param>
    /// <param name="Since">Schema version that introduced the field.</param>
    /// <param name="Required">Whether readers require the field.</param>
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

        public int End => checked(Offset + Length);
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

    /// <summary>Immutable versioned wire layout with a deterministic fingerprint.</summary>
    public sealed class Schema
    {
        public Schema(int version, IReadOnlyList<MessageSchema> messages)
        {
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version), version, "Versions start at 1.");
            ArgumentNullException.ThrowIfNull(messages);
            if (messages.Count == 0)
                throw new ArgumentException("A schema needs at least one message.", nameof(messages));

            Version = version;
            var copy = new MessageSchema[messages.Count];

            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i] ??
                    throw new ArgumentException("Message entries cannot be null.", nameof(messages));
                if (message.Fields is null)
                    throw new ArgumentException($"{message.Name} has no field collection.", nameof(messages));

                copy[i] = new MessageSchema(message.Name, message.TypeCode,
                    Array.AsReadOnly(message.Fields.ToArray()));
            }

            Messages = Array.AsReadOnly(copy);

            var duplicateCodes = Messages.GroupBy(m => m.TypeCode).FirstOrDefault(g => g.Count() > 1);
            if (duplicateCodes is not null)
                throw new ArgumentException($"Type code {duplicateCodes.Key} is used by more than one message.",
                    nameof(messages));
            if (Messages.Select(message => message.Name).Distinct(StringComparer.Ordinal).Count() !=
                Messages.Count)
                throw new ArgumentException("Message names must be unique.", nameof(messages));

            foreach (var message in Messages)
                Validate(message, version);

            Fingerprint = ComputeFingerprint(Messages);
        }

        public int Version { get; }
        public IReadOnlyList<MessageSchema> Messages { get; }

        /// <summary>Stable layout identity for compatibility and session negotiation.</summary>
        public UInt128 Fingerprint { get; }

        public MessageSchema Find(byte typeCode)
            => Messages.FirstOrDefault(message => message.TypeCode == typeCode);

        private static void Validate(MessageSchema message, int version)
        {
            if (string.IsNullOrWhiteSpace(message.Name) || message.TypeCode == 0 ||
                message.Fields.Count == 0)
                throw new ArgumentException("Messages require a name, type code, and fields.");
            if (message.Fields.Any(field => field is null))
                throw new ArgumentException($"{message.Name} contains a null field.");

            var ordered = message.Fields.OrderBy(field => field.Offset).ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var field = ordered[i];

                if (string.IsNullOrWhiteSpace(field.Name))
                    throw new ArgumentException($"{message.Name} contains an unnamed field.");

                if (field.Offset < 0)
                    throw new ArgumentException($"{message.Name}.{field.Name} has a negative offset.");

                if (field.Length <= 0)
                    throw new ArgumentException($"{message.Name}.{field.Name} has a non-positive length.");

                if (field.Since < 1 || field.Since > version)
                    throw new ArgumentException($"{message.Name}.{field.Name} has an invalid version.");

                if (!Enum.IsDefined(field.Type))
                    throw new ArgumentException($"{message.Name}.{field.Name} has an invalid type.");

                try { _ = field.End; }
                catch (OverflowException)
                {
                    throw new ArgumentException($"{message.Name}.{field.Name} exceeds the layout bound.");
                }

                if (field.Type != FieldType.Ascii && field.Length != SchemaField.WidthOf(field.Type))
                    throw new ArgumentException(
                        $"{message.Name}.{field.Name} is {field.Type} but declares {field.Length} bytes.");

                if (i > 0 && ordered[i - 1].End > field.Offset)
                    throw new ArgumentException(
                        $"{message.Name}.{field.Name} at {field.Offset} overlaps " +
                        $"{ordered[i - 1].Name} which ends at {ordered[i - 1].End}.");
            }

            if (message.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() !=
                message.Fields.Count)
                throw new ArgumentException($"{message.Name} has duplicate field names.");
        }

        /// <summary>Truncated SHA-256 over a length-delimited binary canonical form.</summary>
        private static UInt128 ComputeFingerprint(IReadOnlyList<MessageSchema> messages)
        {
            var canonical = new ArrayBufferWriter<byte>();

            void WriteByte(byte value)
            {
                canonical.GetSpan(1)[0] = value;
                canonical.Advance(1);
            }

            void WriteInt32(int value)
            {
                BinaryPrimitives.WriteInt32LittleEndian(canonical.GetSpan(sizeof(int)), value);
                canonical.Advance(sizeof(int));
            }

            void WriteString(string text)
            {
                var length = Encoding.UTF8.GetByteCount(text);
                WriteInt32(length);
                Encoding.UTF8.GetBytes(text, canonical.GetSpan(length));
                canonical.Advance(length);
            }

            WriteInt32(messages.Count);

            foreach (var message in messages.OrderBy(m => m.TypeCode))
            {
                WriteString(message.Name);
                WriteByte(message.TypeCode);
                WriteInt32(message.Fields.Count);

                foreach (var field in message.Fields.OrderBy(f => f.Offset))
                {
                    WriteString(field.Name);
                    WriteByte((byte)field.Type);
                    WriteInt32(field.Offset);
                    WriteInt32(field.Length);
                    WriteInt32(field.Since);
                    WriteByte(field.Required ? (byte)1 : (byte)0);
                }
            }

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(canonical.WrittenSpan, digest);
            return BinaryPrimitives.ReadUInt128LittleEndian(digest);
        }
    }
}
