using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketData.Common.Governance
{
    /// <summary>How one schema version relates to another.</summary>
    public enum CompatibilityKind
    {
        /// <summary>Identical layout.</summary>
        Identical,

        /// <summary>
        /// A reader of the old version can still read the new one. Achieved by adding optional
        /// fields after everything the old reader knows about.
        /// </summary>
        BackwardCompatible,

        /// <summary>
        /// A reader of the new version can read the old one, but not the reverse.
        /// </summary>
        ForwardCompatible,

        /// <summary>Neither direction is safe. Requires a coordinated cutover.</summary>
        Breaking,
    }

    public sealed record CompatibilityBreak(string Message, string MessageType, string Field);

    public sealed record CompatibilityReport(
        CompatibilityKind Kind,
        IReadOnlyList<CompatibilityBreak> Breaks)
    {
        public bool CanDeployIndependently =>
            Kind is CompatibilityKind.Identical or CompatibilityKind.BackwardCompatible;
    }

    /// <summary>
    /// Decides mechanically whether a schema change can be rolled out without a cutover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the question "is this change safe?" is asked at review time, answered
    /// from memory, and answered wrong. The rules are simple enough to state and therefore simple
    /// enough to check:
    /// </para>
    /// <list type="bullet">
    /// <item>Removing a message type or a field breaks readers that use it.</item>
    /// <item>Moving a field breaks every reader, because offsets are the contract.</item>
    /// <item>Changing a field's type or width breaks every reader, for the same reason.</item>
    /// <item>Adding a <em>required</em> field breaks old readers, which will not populate it.</item>
    /// <item>
    /// Adding an <em>optional</em> field beyond the end of the old layout is safe: old readers
    /// stop where they always did and never see it.
    /// </item>
    /// <item>
    /// Adding an optional field <em>inside</em> the old layout is not safe, even though it sounds
    /// like it should be - it necessarily displaces something.
    /// </item>
    /// </list>
    /// <para>
    /// Renaming counts as remove-plus-add, deliberately. A rename is invisible on the wire but
    /// changes what code binds to, and treating it as safe is how a field quietly comes to mean
    /// something different from what its readers assume.
    /// </para>
    /// </remarks>
    public static class Compatibility
    {
        public static CompatibilityReport Compare(Schema older, Schema newer)
        {
            ArgumentNullException.ThrowIfNull(older);
            ArgumentNullException.ThrowIfNull(newer);

            if (older.Fingerprint == newer.Fingerprint)
                return new CompatibilityReport(CompatibilityKind.Identical, Array.Empty<CompatibilityBreak>());

            var breaks = new List<CompatibilityBreak>();
            var additionsOnly = true;

            foreach (var oldMessage in older.Messages)
            {
                var newMessage = newer.Find(oldMessage.TypeCode);

                if (newMessage is null)
                {
                    breaks.Add(new CompatibilityBreak(
                        $"message type {oldMessage.TypeCode} ({oldMessage.Name}) was removed",
                        oldMessage.Name, null));
                    additionsOnly = false;
                    continue;
                }

                if (!string.Equals(oldMessage.Name, newMessage.Name, StringComparison.Ordinal))
                {
                    breaks.Add(new CompatibilityBreak(
                        $"message type {oldMessage.TypeCode} was renamed from {oldMessage.Name} to {newMessage.Name}",
                        oldMessage.Name, null));
                    additionsOnly = false;
                }

                var oldSize = oldMessage.Size;

                foreach (var oldField in oldMessage.Fields)
                {
                    var newField = newMessage.Fields
                        .FirstOrDefault(f => string.Equals(f.Name, oldField.Name, StringComparison.Ordinal));

                    if (newField is null)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} was removed or renamed",
                            oldMessage.Name, oldField.Name));
                        additionsOnly = false;
                        continue;
                    }

                    if (newField.Offset != oldField.Offset)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} moved from offset {oldField.Offset} to {newField.Offset}",
                            oldMessage.Name, oldField.Name));
                        additionsOnly = false;
                    }

                    if (newField.Type != oldField.Type || newField.Length != oldField.Length)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} changed from {oldField.Type}({oldField.Length}) " +
                            $"to {newField.Type}({newField.Length})",
                            oldMessage.Name, oldField.Name));
                        additionsOnly = false;
                    }
                }

                // Anything new in an existing message must be optional and must sit beyond where
                // the old reader stops.
                foreach (var addedField in newMessage.Fields.Where(f =>
                             oldMessage.Fields.All(o => !string.Equals(o.Name, f.Name, StringComparison.Ordinal))))
                {
                    if (addedField.Required)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"required field {addedField.Name} was added; old writers will not populate it",
                            newMessage.Name, addedField.Name));
                        additionsOnly = false;
                    }
                    else if (addedField.Offset < oldSize)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"optional field {addedField.Name} was added at offset {addedField.Offset}, " +
                            $"inside the previous layout which ended at {oldSize}",
                            newMessage.Name, addedField.Name));
                        additionsOnly = false;
                    }
                }
            }

            if (breaks.Count > 0)
                return new CompatibilityReport(CompatibilityKind.Breaking, breaks);

            // No breaks. Whether new message types were added decides the direction: an old reader
            // copes with them only by ignoring unknown type codes, which this protocol does.
            var addedMessages = newer.Messages
                .Any(m => older.Find(m.TypeCode) is null);

            var kind = additionsOnly
                ? CompatibilityKind.BackwardCompatible
                : CompatibilityKind.ForwardCompatible;

            return new CompatibilityReport(
                addedMessages ? CompatibilityKind.BackwardCompatible : kind,
                Array.Empty<CompatibilityBreak>());
        }

        /// <summary>
        /// Throws unless <paramref name="newer"/> can be deployed without a coordinated cutover.
        /// </summary>
        /// <remarks>
        /// Meant to be called from a test, so a breaking change fails the build with the reasons
        /// listed rather than being discovered by a subscriber in production.
        /// </remarks>
        public static void AssertDeployableAgainst(Schema older, Schema newer)
        {
            var report = Compare(older, newer);

            if (report.CanDeployIndependently)
                return;

            var detail = string.Join(Environment.NewLine,
                report.Breaks.Select(b => $"  - {b.MessageType}: {b.Message}"));

            throw new SchemaCompatibilityException(
                $"schema v{older.Version} -> v{newer.Version} is {report.Kind}:{Environment.NewLine}{detail}",
                report);
        }
    }

    public sealed class SchemaCompatibilityException : Exception
    {
        public SchemaCompatibilityException(string message, CompatibilityReport report) : base(message)
            => Report = report;

        public CompatibilityReport Report { get; }
    }
}
