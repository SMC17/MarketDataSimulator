using System;
using System.Collections.Generic;
using System.Linq;
using MarketData.Common.Feed;

namespace MarketData.Common.Governance
{
    /// <summary>Machine-readable layouts mirrored from <c>FeedProtocol</c>.</summary>
    public static class FeedSchemas
    {
        public const byte AddTypeCode = (byte)FeedMessageType.Add;
        public const byte ReplaceTypeCode = (byte)FeedMessageType.Replace;
        public const byte RemoveTypeCode = (byte)FeedMessageType.Remove;
        public const byte SnapshotHeaderTypeCode = (byte)FeedMessageType.Snapshot;
        public const byte HeartbeatTypeCode = (byte)FeedMessageType.Heartbeat;

        /// <summary>Version 1: the original layout, retained so compatibility can be tested.</summary>
        public static Schema V1 { get; } = new(1, new[]
        {
            Incremental("Add", AddTypeCode),
            Incremental("Replace", ReplaceTypeCode),
            Incremental("Remove", RemoveTypeCode),
            new MessageSchema("SnapshotHeader", SnapshotHeaderTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("InstrumentId", FieldType.Int32, 1, 4),
                new SchemaField("BidLevels", FieldType.UInt8, 5, 1),
                new SchemaField("AskLevels", FieldType.UInt8, 6, 1),
            }),
        });

        /// <summary>Current layout, including heartbeat.</summary>
        public static Schema V2 { get; } = new(2, new[]
        {
            Incremental("Add", AddTypeCode),
            Incremental("Replace", ReplaceTypeCode),
            Incremental("Remove", RemoveTypeCode),
            new MessageSchema("SnapshotHeader", SnapshotHeaderTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("InstrumentId", FieldType.Int32, 1, 4),
                new SchemaField("BidLevels", FieldType.UInt8, 5, 1),
                new SchemaField("AskLevels", FieldType.UInt8, 6, 1),
            }),
            new MessageSchema("Heartbeat", HeartbeatTypeCode, new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1, Since: 2),
            }),
        });

        public static Schema Current => V2;

        public static IReadOnlyList<Schema> All { get; } =
            Array.AsReadOnly(new[] { V1, V2 });

        private static MessageSchema Incremental(string name, byte typeCode) => new(name, typeCode,
            new[]
            {
                new SchemaField("MessageType", FieldType.UInt8, 0, 1),
                new SchemaField("InstrumentId", FieldType.Int32, 1, 4),
                new SchemaField("Price", FieldType.Int32, 5, 4),
                new SchemaField("Quantity", FieldType.UInt32, 9, 4),
                new SchemaField("Side", FieldType.UInt8, 13, 1),
            });
    }

    /// <summary>Immutable registry for session-start schema negotiation.</summary>
    public sealed class SchemaRegistry
    {
        private readonly Dictionary<int, Schema> _byVersion;

        public SchemaRegistry(IEnumerable<Schema> schemas)
        {
            ArgumentNullException.ThrowIfNull(schemas);
            var materialized = schemas.ToArray();
            if (materialized.Any(schema => schema is null))
                throw new ArgumentException("Schema entries cannot be null.", nameof(schemas));

            _byVersion = materialized.ToDictionary(schema => schema.Version);

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

        /// <summary>Returns the highest shared version.</summary>
        public Schema Negotiate(IEnumerable<int> peerVersions)
        {
            ArgumentNullException.ThrowIfNull(peerVersions);
            var peer = peerVersions.Distinct().ToArray();
            var shared = peer.Where(_byVersion.ContainsKey).ToList();

            if (shared.Count == 0)
            {
                throw new SchemaNegotiationException(
                    $"no shared schema version: this side speaks [{string.Join(", ", Versions)}], " +
                    $"the peer speaks [{string.Join(", ", peer)}]");
            }

            return _byVersion[shared.Max()];
        }

        /// <summary>Validates a peer's version fingerprint.</summary>
        public Schema Confirm(int version, UInt128 peerFingerprint)
        {
            var schema = Get(version);

            if (schema.Fingerprint != peerFingerprint)
            {
                throw new SchemaNegotiationException(
                    $"schema v{version} fingerprint mismatch: peer sent {peerFingerprint:X32}, " +
                    $"this build has {schema.Fingerprint:X32}. Same version number, different layout.");
            }

            return schema;
        }
    }

    public sealed class SchemaNegotiationException : Exception
    {
        public SchemaNegotiationException(string message) : base(message) { }
    }
}
