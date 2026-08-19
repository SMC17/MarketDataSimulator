using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketData.Common.Governance
{
    /// <summary>
    /// The wire contract of the live feed, declared as data so it can be diffed.
    /// </summary>
    /// <remarks>
    /// These layouts mirror <c>FeedProtocol</c>. Keeping them in step is enforced by
    /// <c>SchemaGovernanceTests</c>, which compares the declared sizes against the constants the
    /// encoder actually uses - a schema that has drifted from its encoder is worse than no schema,
    /// because it will be believed.
    /// </remarks>
    public static class FeedSchemas
    {
        public const byte IncrementalTypeCode = 1;
        public const byte SnapshotHeaderTypeCode = 4;
        public const byte HeartbeatTypeCode = 5;

        /// <summary>Version 1: the original layout, retained so compatibility can be tested.</summary>
        public static Schema V1 { get; } = new(1, new[]
        {
            new MessageSchema("Incremental", IncrementalTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("Side", FieldType.UInt8, 1, 1),
                new SchemaField("InstrumentId", FieldType.UInt32, 2, 4),
                new SchemaField("Price", FieldType.Int32, 6, 4),
                new SchemaField("Quantity", FieldType.UInt32, 10, 4),
            }),
            new MessageSchema("SnapshotHeader", SnapshotHeaderTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("InstrumentId", FieldType.UInt32, 1, 4),
                new SchemaField("BidLevels", FieldType.UInt8, 5, 1),
                new SchemaField("AskLevels", FieldType.UInt8, 6, 1),
            }),
        });

        /// <summary>
        /// Version 2: the current layout, adding a heartbeat message.
        /// </summary>
        /// <remarks>
        /// Adding a whole message type is backward compatible in this protocol because unknown
        /// type codes are skipped rather than fatal - which is a property of the decoder, not a
        /// law of nature, and is asserted by test.
        /// </remarks>
        public static Schema V2 { get; } = new(2, new[]
        {
            new MessageSchema("Incremental", IncrementalTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("Side", FieldType.UInt8, 1, 1),
                new SchemaField("InstrumentId", FieldType.UInt32, 2, 4),
                new SchemaField("Price", FieldType.Int32, 6, 4),
                new SchemaField("Quantity", FieldType.UInt32, 10, 4),
            }),
            new MessageSchema("SnapshotHeader", SnapshotHeaderTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("InstrumentId", FieldType.UInt32, 1, 4),
                new SchemaField("BidLevels", FieldType.UInt8, 5, 1),
                new SchemaField("AskLevels", FieldType.UInt8, 6, 1),
            }),
            new MessageSchema("Heartbeat", HeartbeatTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1, Since: 2),
            }),
        });

        public static Schema Current => V2;

        public static IReadOnlyList<Schema> All { get; } = new[] { V1, V2 };
    }

    /// <summary>
    /// Holds the known schema versions and settles which one a session speaks.
    /// </summary>
    /// <remarks>
    /// Negotiation happens once, at session start, and its result is a single version for the life
    /// of the session. Renegotiating mid-stream would mean a subscriber's decoder changing shape
    /// while sequenced messages are in flight, which is unresolvable: a packet already on the wire
    /// belongs to the old layout and there is no way to say so after the fact.
    /// </remarks>
    public sealed class SchemaRegistry
    {
        private readonly Dictionary<int, Schema> _byVersion;

        public SchemaRegistry(IEnumerable<Schema> schemas)
        {
            _byVersion = schemas.ToDictionary(schema => schema.Version);

            if (_byVersion.Count == 0)
                throw new ArgumentException("A registry needs at least one schema.", nameof(schemas));
        }

        public static SchemaRegistry Default { get; } = new(FeedSchemas.All);

        public IEnumerable<int> Versions => _byVersion.Keys.OrderBy(v => v);

        public Schema Get(int version)
            => _byVersion.TryGetValue(version, out var schema)
                ? schema
                : throw new KeyNotFoundException($"No schema at version {version}.");

        public bool TryGet(int version, out Schema schema) => _byVersion.TryGetValue(version, out schema);

        /// <summary>
        /// Settles on the highest version both sides know.
        /// </summary>
        /// <remarks>
        /// Fails rather than falling back when there is no overlap. A silent downgrade to a version
        /// the publisher no longer intends to speak is how a subscriber ends up quietly consuming a
        /// contract nobody is testing.
        /// </remarks>
        public Schema Negotiate(IEnumerable<int> peerVersions)
        {
            var shared = peerVersions.Where(_byVersion.ContainsKey).ToList();

            if (shared.Count == 0)
            {
                throw new SchemaNegotiationException(
                    $"no shared schema version: this side speaks [{string.Join(", ", Versions)}], " +
                    $"the peer speaks [{string.Join(", ", peerVersions)}]");
            }

            return _byVersion[shared.Max()];
        }

        /// <summary>
        /// Confirms the peer's fingerprint matches what this side believes that version looks like.
        /// </summary>
        /// <remarks>
        /// The check that catches the genuinely dangerous case: two builds that agree they speak
        /// "version 2" while disagreeing about what version 2 is, because one of them shipped a
        /// layout edit without bumping the number. The version is what they agree on; the
        /// fingerprint is what makes the agreement mean something.
        /// </remarks>
        public Schema Confirm(int version, ulong peerFingerprint)
        {
            var schema = Get(version);

            if (schema.Fingerprint != peerFingerprint)
            {
                throw new SchemaNegotiationException(
                    $"schema v{version} fingerprint mismatch: peer sent {peerFingerprint:X16}, " +
                    $"this build has {schema.Fingerprint:X16}. Same version number, different layout.");
            }

            return schema;
        }
    }

    public sealed class SchemaNegotiationException : Exception
    {
        public SchemaNegotiationException(string message) : base(message) { }
    }
}
