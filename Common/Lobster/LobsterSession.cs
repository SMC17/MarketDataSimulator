using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MarketData.Common.Lobster
{
    /// <summary>One instrument-day: a message file and the reference book it produced.</summary>
    public sealed record LobsterSession(string Symbol, string Date, int Levels, string MessagePath, string ReferencePath)
    {
        public string Name => $"{Symbol} L{Levels}";

        public override string ToString() => $"{Symbol} {Date} level {Levels}";
    }

    /// <summary>
    /// Finds message/orderbook pairs in a directory.
    /// </summary>
    /// <remarks>
    /// LOBSTER names files <c>SYMBOL_DATE_START_END_kind_LEVELS.csv</c>, so a directory of them is
    /// self-describing and several instrument-days can sit side by side. Pairing is by everything
    /// except the kind, which is what makes a mismatched pair impossible rather than merely
    /// unlikely.
    /// </remarks>
    public static class LobsterSessions
    {
        public static IReadOnlyList<LobsterSession> Discover(string directory)
        {
            if (!Directory.Exists(directory))
                return Array.Empty<LobsterSession>();

            var sessions = new List<LobsterSession>();

            foreach (var messagePath in Directory.GetFiles(directory, "*_message_*.csv").OrderBy(p => p))
            {
                var referencePath = messagePath.Replace("_message_", "_orderbook_");

                if (!File.Exists(referencePath))
                    continue;

                var parts = Path.GetFileNameWithoutExtension(messagePath).Split('_');

                if (parts.Length < 6 || !int.TryParse(parts[^1], out var levels))
                    continue;

                sessions.Add(new LobsterSession(parts[0], parts[1], levels, messagePath, referencePath));
            }

            return sessions;
        }
    }
}
