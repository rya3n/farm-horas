using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FarmHoras
{
    internal sealed class SavedGames
    {
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };
        private readonly string directory;
        private readonly string path;
        private readonly string logPath;

        internal SavedGames(string directory, string logPath)
        {
            this.directory = directory;
            this.logPath = logPath;
            path = Path.Combine(directory, "saved-appids.txt");
        }

        internal List<uint> Load()
        {
            string invalid;
            if (File.Exists(path))
            {
                return ParseIds(File.ReadAllText(path), out invalid);
            }

            string legacyPath = Path.Combine(directory, "appids.txt");
            var ids = File.Exists(legacyPath)
                ? ParseIds(File.ReadAllText(legacyPath), out invalid)
                : new List<uint>();

            if (File.Exists(logPath))
            {
                RecoverPreviousSessions(ids);
            }

            if (ids.Count > 0)
            {
                Save(ids);
            }

            return ids;
        }

        private void RecoverPreviousSessions(List<uint> ids)
        {
            const string separator = " | ";
            const string started = " | Sessão iniciada.";
            var seen = new HashSet<uint>(ids);
            foreach (string line in File.ReadLines(logPath))
            {
                if (!line.EndsWith(started, StringComparison.Ordinal))
                {
                    continue;
                }

                int from = line.IndexOf(separator, StringComparison.Ordinal) + separator.Length;
                int to = line.Length - started.Length;
                uint id;
                if (from > separator.Length && to > from && TryParseId(line.Substring(from, to - from), out id) && seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        internal void Save(IEnumerable<uint> ids)
        {
            Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary, false, Encoding.UTF8))
            {
                foreach (uint id in ids)
                {
                    writer.WriteLine(id.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        internal static List<uint> ParseIds(string input, out string invalid)
        {
            var result = new List<uint>();
            var seen = new HashSet<uint>();
            invalid = null;
            foreach (string token in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                uint id;
                if (!TryParseId(token, out id))
                {
                    invalid = invalid ?? token;
                }
                else if (seen.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        private static bool TryParseId(string token, out uint id)
        {
            return uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }
    }
}
