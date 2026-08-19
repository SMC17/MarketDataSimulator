using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

            foreach (var messagePath in Directory.GetFiles(directory, "*_message_*.csv*").OrderBy(p => p))
            {
                if (!messagePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                    !messagePath.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
                    continue;

                var referencePath = messagePath.Replace("_message_", "_orderbook_");

                if (!File.Exists(referencePath))
                    continue;

                var filename = Path.GetFileNameWithoutExtension(messagePath);

                if (filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    filename = Path.GetFileNameWithoutExtension(filename);

                var parts = filename.Split('_');
                string date;
                int levels;

                if (parts.Length >= 6 && int.TryParse(parts[^1], out levels))
                {
                    date = parts[1];
                }
                else
                {
                    levels = LobsterReplay.DetectLevels(ReadAllBytes(referencePath));
                    date = "sample";

                    if (levels == 0)
                        continue;
                }

                sessions.Add(new LobsterSession(parts[0], date, levels, messagePath, referencePath));
            }

            return sessions;
        }

        public static byte[] ReadAllBytes(string path)
        {
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(path);

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }
}
