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

        /// <summary>Old readers can consume the new layout.</summary>
        BackwardCompatible,

        /// <summary>New readers can consume the old layout.</summary>
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

    /// <summary>Classifies schema changes and reports field-level breaks.</summary>
    public static class Compatibility
    {
        public static CompatibilityReport Compare(Schema older, Schema newer)
        {
            ArgumentNullException.ThrowIfNull(older);
            ArgumentNullException.ThrowIfNull(newer);

            if (SameLayout(older, newer))
                return new CompatibilityReport(CompatibilityKind.Identical, Array.Empty<CompatibilityBreak>());

            var breaks = new List<CompatibilityBreak>();
            foreach (var oldMessage in older.Messages)
            {
                var newMessage = newer.Find(oldMessage.TypeCode);

                if (newMessage is null)
                {
                    breaks.Add(new CompatibilityBreak(
                        $"message type {oldMessage.TypeCode} ({oldMessage.Name}) was removed",
                        oldMessage.Name, null));
                    continue;
                }

                if (!string.Equals(oldMessage.Name, newMessage.Name, StringComparison.Ordinal))
                {
                    breaks.Add(new CompatibilityBreak(
                        $"message type {oldMessage.TypeCode} was renamed from {oldMessage.Name} to {newMessage.Name}",
                        oldMessage.Name, null));
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
                        continue;
                    }

                    if (newField.Offset != oldField.Offset)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} moved from offset {oldField.Offset} to {newField.Offset}",
                            oldMessage.Name, oldField.Name));
                    }

                    if (newField.Type != oldField.Type || newField.Length != oldField.Length)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} changed from {oldField.Type}({oldField.Length}) " +
                            $"to {newField.Type}({newField.Length})",
                            oldMessage.Name, oldField.Name));
                    }

                    if (newField.Since != oldField.Since || newField.Required != oldField.Required)
                    {
                        breaks.Add(new CompatibilityBreak(
                            $"field {oldField.Name} changed evolution metadata",
                            oldMessage.Name, oldField.Name));
                    }
                }

                foreach (var addedField in newMessage.Fields.Where(f =>
                             oldMessage.Fields.All(o => !string.Equals(o.Name, f.Name, StringComparison.Ordinal))))
                {
                    var location = addedField.Offset < oldSize
                        ? $"inside the previous layout which ended at {oldSize}"
                        : "to a message without a length-delimited envelope";
                    breaks.Add(new CompatibilityBreak(
                        $"{(addedField.Required ? "required" : "optional")} field " +
                        $"{addedField.Name} was added {location}",
                        newMessage.Name, addedField.Name));
                }
            }

            if (breaks.Count > 0)
                return new CompatibilityReport(CompatibilityKind.Breaking, breaks);

            var addedMessages = newer.Messages.Where(message => older.Find(message.TypeCode) is null)
                .ToArray();

            if (addedMessages.Length > 0)
            {
                return new CompatibilityReport(CompatibilityKind.ForwardCompatible,
                    addedMessages.Select(message => new CompatibilityBreak(
                        $"message type {message.TypeCode} ({message.Name}) was added; " +
                        "old readers cannot skip unframed message types",
                        message.Name, null)).ToArray());
            }

            return new CompatibilityReport(CompatibilityKind.Breaking, new[]
            {
                new CompatibilityBreak("fingerprints differ without a classified layout change", null, null),
            });
        }

        private static bool SameLayout(Schema left, Schema right)
        {
            if (left.Messages.Count != right.Messages.Count)
                return false;

            foreach (var leftMessage in left.Messages)
            {
                var rightMessage = right.Find(leftMessage.TypeCode);
                if (rightMessage is null ||
                    !string.Equals(leftMessage.Name, rightMessage.Name, StringComparison.Ordinal) ||
                    leftMessage.Fields.Count != rightMessage.Fields.Count)
                    return false;

                foreach (var leftField in leftMessage.Fields)
                {
                    var rightField = rightMessage.Fields.FirstOrDefault(field =>
                        string.Equals(field.Name, leftField.Name, StringComparison.Ordinal));

                    if (rightField is null || rightField.Type != leftField.Type ||
                        rightField.Offset != leftField.Offset || rightField.Length != leftField.Length ||
                        rightField.Since != leftField.Since || rightField.Required != leftField.Required)
                        return false;
                }
            }

            return true;
        }

        /// <summary>Throws when independent deployment is unsafe.</summary>
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
