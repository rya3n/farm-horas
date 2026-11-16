using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FarmHoras
{
    internal sealed class SteamConfigWatcher : IDisposable
    {
        private static readonly long QuietPeriod = Stopwatch.Frequency / 4;
        private volatile FileSystemWatcher watcher;
        private string path;
        private long lastChange;
        private int needsReopen;

        internal void Follow(string configPath)
        {
            if (string.Equals(path, configPath, StringComparison.OrdinalIgnoreCase) &&
                Volatile.Read(ref needsReopen) == 0)
            {
                return;
            }

            Dispose();
            if (configPath == null)
            {
                return;
            }

            try
            {
                var next = new FileSystemWatcher(Path.GetDirectoryName(configPath), Path.GetFileName(configPath));
                watcher = next;
                next.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                next.Changed += OnChange;
                next.Created += OnChange;
                next.Renamed += OnChange;
                next.Deleted += OnChange;
                next.Error += OnError;
                next.EnableRaisingEvents = true;
                path = configPath;
                // Cover a write that raced with the first read and watcher setup.
                OnChange(next, null);
            }
            catch (ArgumentException)
            {
                Dispose();
            }
            catch (IOException)
            {
                Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                Dispose();
            }
            catch (Win32Exception)
            {
                Dispose();
            }
        }

        internal bool TakeChange()
        {
            long change = Interlocked.Read(ref lastChange);
            return change != 0 && Stopwatch.GetTimestamp() - change >= QuietPeriod &&
                Interlocked.CompareExchange(ref lastChange, 0, change) == change;
        }

        private void OnChange(object sender, FileSystemEventArgs args)
        {
            if (ReferenceEquals(sender, watcher))
            {
                Interlocked.Exchange(ref lastChange, Stopwatch.GetTimestamp());
            }
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            if (ReferenceEquals(sender, watcher))
            {
                Volatile.Write(ref needsReopen, 1);
                OnChange(sender, null);
            }
        }

        public void Dispose()
        {
            var previous = watcher;
            watcher = null;
            path = null;
            Interlocked.Exchange(ref lastChange, 0);
            Volatile.Write(ref needsReopen, 0);
            if (previous != null)
            {
                previous.Dispose();
            }
        }
    }
}
