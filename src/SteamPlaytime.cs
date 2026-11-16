using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FarmHoras
{
    internal sealed class SteamPlaytime
    {
        internal const int TargetMinutes = 3333 * 60;
        internal const int TargetSeconds = TargetMinutes * 60;
        private const ulong SteamIdBase = 76561197960265728UL;
        private static readonly CultureInfo HoursCulture = CultureInfo.GetCultureInfo("pt-BR");
        private string cachedPath;
        private DateTime cachedWriteTime;
        private long cachedLength;
        private Dictionary<uint, int> cachedTotals;
        internal uint AccountId { get; private set; }
        internal string ConfigPath { get; private set; }

        internal Dictionary<uint, int> Read(bool forceRead = false)
        {
            AccountId = 0;
            ConfigPath = null;
            try
            {
                using (var client = SteamClient.FindRunning())
                {
                    if (client == null)
                    {
                        return null;
                    }

                    string install = Path.GetDirectoryName(client.MainModule.FileName);
                    uint account = ResolveAccount(client.Id, install);

                    if (account == 0)
                    {
                        return null;
                    }

                    var file = new FileInfo(Path.Combine(install, "userdata", account.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf"));
                    ConfigPath = file.FullName;
                    DateTime writeTime = file.LastWriteTimeUtc;
                    long length = file.Length;
                    if (!forceRead && file.FullName == cachedPath && writeTime == cachedWriteTime && length == cachedLength)
                    {
                        AccountId = account;
                        return cachedTotals;
                    }

                    var totals = ParseTotals(Vdf.Read(file.FullName));
                    file.Refresh();
                    if (totals == null || file.LastWriteTimeUtc != writeTime || file.Length != length)
                    {
                        return null;
                    }

                    cachedPath = file.FullName;
                    cachedWriteTime = writeTime;
                    cachedLength = length;
                    cachedTotals = totals;
                    AccountId = account;
                    return totals;
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (SecurityException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        internal static uint ReadCurrentAccount()
        {
            using (var client = SteamClient.FindRunning())
            {
                return client == null ? 0 : ResolveAccount(client.Id, Path.GetDirectoryName(client.MainModule.FileName));
            }
        }

        private static uint ResolveAccount(int steamPid, string installation)
        {
            return ReadActiveAccount(steamPid) ??
                ReadLoginAccount(Vdf.Read(Path.Combine(installation, "config", "loginusers.vdf")));
        }

        private static uint? ReadActiveAccount(int steamPid)
        {
            using (var registry = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                if (registry != null && object.Equals(registry.GetValue("pid"), steamPid))
                {
                    object active = registry.GetValue("ActiveUser");
                    if (active is int)
                    {
                        return unchecked((uint)(int)active);
                    }
                }
            }

            return null;
        }

        internal static uint ReadLoginAccount(Vdf root)
        {
            var users = root.Get("users");
            if (users == null)
            {
                return 0;
            }

            uint sole = 0;
            foreach (var item in users.Children)
            {
                ulong steamId;
                if (!ulong.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) ||
                    steamId <= SteamIdBase || steamId > SteamIdBase + uint.MaxValue)
                {
                    continue;
                }

                if (sole != 0)
                {
                    return 0;
                }
                sole = (uint)(steamId - SteamIdBase);
            }

            return sole;
        }

        internal static Dictionary<uint, int> ParseTotals(Vdf root)
        {
            var apps = root.Get("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            if (apps == null)
            {
                return null;
            }

            var totals = new Dictionary<uint, int>();
            foreach (var item in apps.Children)
            {
                uint id;
                int minutes;
                if (uint.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 &&
                    int.TryParse(item.Value.Text("Playtime"), NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
                {
                    totals[id] = minutes;
                }
            }

            return totals;
        }

        internal static string FormatMinutes(long minutes)
        {
            return string.Format(HoursCulture, "{0:N0} h {1:00} min", minutes / 60, minutes % 60);
        }
    }

    internal sealed class Vdf
    {
        private static readonly Regex Token = new Regex(
            "\\G\\s*(?:(?<comment>//[^\\r\\n]*)|(?<quoted>\"(?:\\\\.|[^\"\\\\])*\")|(?<brace>[{}])|(?<bare>[^\\s\"{}]+)|(?<end>\\z))",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private readonly string value;
        internal readonly Dictionary<string, Vdf> Children = new Dictionary<string, Vdf>(StringComparer.OrdinalIgnoreCase);

        private Vdf(string value = null)
        {
            this.value = value;
        }

        internal Vdf Get(params string[] path)
        {
            Vdf node = this;
            foreach (string key in path)
            {
                Vdf child;
                if (!node.Children.TryGetValue(key, out child))
                {
                    return null;
                }

                node = child;
            }

            return node;
        }

        internal string Text(string key)
        {
            var node = Get(key);
            return node == null ? null : node.value;
        }

        internal static Vdf Read(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                if (stream.Length > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("Arquivo VDF excede o tamanho esperado.");
                }

                string content = reader.ReadToEnd();
                int offset = 0;
                return ReadObject(content, ref offset, 0);
            }
        }

        private static Vdf ReadObject(string input, ref int offset, int depth)
        {
            if (depth > 32)
            {
                throw new InvalidDataException("Arquivo VDF possui blocos aninhados demais.");
            }

            var node = new Vdf();
            while (true)
            {
                Match key = ReadToken(input, ref offset);
                if (depth == 0 ? key.Groups["end"].Success : key.Groups["brace"].Value == "}")
                {
                    return node;
                }

                string name = ReadText(key);
                Match entry = ReadToken(input, ref offset);
                node.Children[name] = entry.Groups["brace"].Value == "{"
                    ? ReadObject(input, ref offset, depth + 1)
                    : new Vdf(ReadText(entry));
            }
        }

        private static Match ReadToken(string input, ref int offset)
        {
            Match match;
            do
            {
                match = Token.Match(input, offset);
                if (!match.Success)
                {
                    throw new InvalidDataException("Arquivo VDF incompleto.");
                }

                offset += match.Length;
            }
            while (match.Groups["comment"].Success);
            return match;
        }

        private static string ReadText(Match token)
        {
            var quoted = token.Groups["quoted"];
            if (quoted.Success)
            {
                return quoted.Value.Substring(1, quoted.Length - 2);
            }

            var bare = token.Groups["bare"];
            if (!bare.Success)
            {
                throw new InvalidDataException("Chave ou valor VDF esperado.");
            }

            return bare.Value;
        }
    }
}
