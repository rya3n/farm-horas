using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FarmHoras
{
    internal sealed class IdleSession : IDisposable
    {
        private readonly EventWaitHandle stop;
        private readonly EventWaitHandle ready;
        private readonly Process worker;
        private readonly Stopwatch startup = Stopwatch.StartNew();
        private readonly Stopwatch elapsed = new Stopwatch();
        private readonly Stopwatch stopping = new Stopwatch();

        internal bool Started { get; private set; }
        internal bool Stopping { get; private set; }
        internal bool TimedOut { get; set; }
        internal bool SteamClosed { get; set; }
        internal bool GoalReached { get; set; }
        internal string SafetyReason { get; set; }
        internal PlaytimeBudget Budget { get; private set; }
        internal bool IsExited { get { return worker.HasExited; } }
        internal int ExitCode { get { return worker.ExitCode; } }
        internal TimeSpan Elapsed { get { return elapsed.Elapsed; } }
        internal bool StartExpired { get { return startup.Elapsed.TotalSeconds >= 12; } }
        internal bool StopExpired { get { return stopping.Elapsed.TotalSeconds >= 3; } }

        internal IdleSession(uint appId, PlaytimeBudget budget)
        {
            Budget = budget;
            var channel = Guid.NewGuid();
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(channel, "stop"));
            try
            {
                ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(channel, "ready"));
                using (var parent = Process.GetCurrentProcess())
                {
                    var application = System.Windows.Forms.Application.ExecutablePath;
                    var start = new ProcessStartInfo(application, string.Format(CultureInfo.InvariantCulture,
                        "--idle {0} {1} {2:N} {3} {4} {5}", appId, parent.Id, channel, budget.DeadlineUtc.Ticks,
                        budget.DeadlineTimestamp, budget.AccountId));
                    start.WorkingDirectory = Path.GetDirectoryName(application);
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    worker = Process.Start(start);
                    if (worker == null)
                    {
                        throw new InvalidOperationException("O processo auxiliar não foi iniciado.");
                    }
                }
            }
            catch
            {
                stop.Dispose();
                if (ready != null)
                {
                    ready.Dispose();
                }

                throw;
            }
        }

        internal static string EventName(Guid channel, string kind)
        {
            return @"Local\FarmHoras." + channel.ToString("N") + "." + kind;
        }

        internal bool CheckReady()
        {
            if (Started || Stopping || !ready.WaitOne(0))
            {
                return false;
            }

            Started = true;
            startup.Stop();
            elapsed.Start();
            return true;
        }

        internal void RequestStop()
        {
            if (Stopping)
            {
                return;
            }

            Stopping = true;
            elapsed.Stop();
            stopping.Start();
            stop.Set();
        }

        internal bool WaitForExit(int milliseconds)
        {
            return worker.WaitForExit(milliseconds);
        }

        internal void ForceStop()
        {
            try
            {
                if (!worker.HasExited)
                {
                    worker.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // The worker already exited.
            }
        }

        public void Dispose()
        {
            elapsed.Stop();
            worker.Dispose();
            ready.Dispose();
            stop.Dispose();
        }
    }
}
