using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FarmHoras
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                return IdleWorker.Run(args);
            }

            bool firstInstance;
            using (var instance = new Mutex(true, @"Local\FarmHoras.SteamHourFarm", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("O Farm Horas já está aberto.", "Farm Horas", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainWindow());
                    return 0;
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }
    }

    internal static class SteamClient
    {
        internal static bool IsRunning()
        {
            using (var steam = FindRunning())
            {
                return steam != null;
            }
        }

        internal static Process FindRunning()
        {
            var clients = Process.GetProcessesByName("steam");
            Process running = null;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    foreach (var client in clients)
                    {
                        try
                        {
                            if (!client.HasExited && client.SessionId == current.SessionId)
                            {
                                running = client;
                                return running;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // The client closed while the process list was being read.
                        }
                    }
                }

                return null;
            }
            finally
            {
                foreach (var client in clients)
                {
                    if (client != running)
                    {
                        client.Dispose();
                    }
                }
            }
        }
    }

    internal static class IdleWorker
    {
        internal const string Library = "steam_api64.dll";
        internal const int SteamRejected = 2;
        internal const int LibraryUnavailable = 3;
        internal const int WorkerFailed = 4;
        internal const int SafetyStopped = 5;
        internal const int AccountChanged = 6;
        internal const int CancelledBeforeStart = 7;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_Init();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        private static extern void SteamAPI_RunCallbacks();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        private static extern void SteamAPI_Shutdown();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        internal static System.Threading.Timer StartWatchdog(DateTime deadline, long deadlineTimestamp, Process parent, EventWaitHandle stop)
        {
            long stopTimestamp = 0;
            int checkingStop = 0;
            return new System.Threading.Timer(delegate
            {
                if (PlaytimeBudget.DeadlinePassed(deadline, deadlineTimestamp, PlaytimeGuard.ShutdownSeconds - 2))
                {
                    HardStop(SafetyStopped);
                }

                if (Interlocked.Exchange(ref checkingStop, 1) != 0)
                {
                    return;
                }

                try
                {
                    if (stopTimestamp == 0)
                    {
                        if (!stop.WaitOne(0))
                        {
                            if (!parent.HasExited)
                            {
                                return;
                            }
                            stop.Set();
                        }

                        stopTimestamp = Stopwatch.GetTimestamp();
                    }
                    else if (Stopwatch.GetTimestamp() - stopTimestamp >= Stopwatch.Frequency * (PlaytimeGuard.ShutdownSeconds - 2))
                    {
                        HardStop(WorkerFailed);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The session closed while this callback was running.
                }
                catch (Win32Exception)
                {
                    HardStop(WorkerFailed);
                }
                finally
                {
                    Volatile.Write(ref checkingStop, 0);
                }
            }, null, 250, 250);
        }

        private static void HardStop(int code)
        {
            // Graceful shutdown had its chance. A hung native DLL can block normal process exit.
            TerminateProcess(GetCurrentProcess(), (uint)code);
            Environment.Exit(code);
        }

        internal static int Run(string[] args)
        {
            uint appId;
            int parentId;
            Guid channel;
            long deadlineTicks;
            long deadlineTimestamp;
            uint account;
            if (args.Length != 7 || args[0] != "--idle" ||
                !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId == 0 ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentId) || parentId <= 0 ||
                !Guid.TryParseExact(args[3], "N", out channel) ||
                !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out deadlineTicks) ||
                deadlineTicks <= 0 || deadlineTicks > DateTime.MaxValue.Ticks ||
                !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out deadlineTimestamp) || deadlineTimestamp <= 0 ||
                !uint.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out account) || account == 0)
            {
                return WorkerFailed;
            }

            var deadline = new DateTime(deadlineTicks, DateTimeKind.Utc);
            if (PlaytimeBudget.DeadlinePassed(deadline, deadlineTimestamp))
            {
                return SafetyStopped;
            }

            try
            {
                using (var parent = Process.GetProcessById(parentId))
                using (var stop = EventWaitHandle.OpenExisting(IdleSession.EventName(channel, "stop")))
                using (var ready = EventWaitHandle.OpenExisting(IdleSession.EventName(channel, "ready")))
                {
                    if (parent.HasExited || stop.WaitOne(0))
                    {
                        return CancelledBeforeStart;
                    }

                    using (var watchdog = StartWatchdog(deadline, deadlineTimestamp, parent, stop))
                    {
                        return RunSteam(appId, account, deadline, deadlineTimestamp, stop, ready);
                    }
                }
            }
            catch (DllNotFoundException)
            {
                return LibraryUnavailable;
            }
            catch (BadImageFormatException)
            {
                return LibraryUnavailable;
            }
            catch (EntryPointNotFoundException)
            {
                return LibraryUnavailable;
            }
            catch (Exception)
            {
                return WorkerFailed;
            }
        }

        private static int RunSteam(uint appId, uint account, DateTime deadline, long deadlineTimestamp, EventWaitHandle stop, EventWaitHandle ready)
        {
            if (SteamPlaytime.ReadCurrentAccount() != account)
            {
                return AccountChanged;
            }

            if (stop.WaitOne(0))
            {
                return CancelledBeforeStart;
            }

            if (PlaytimeBudget.DeadlinePassed(deadline, deadlineTimestamp))
            {
                return SafetyStopped;
            }

            // Steamworks keeps one AppID per process.
            var id = appId.ToString(CultureInfo.InvariantCulture);
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
            if (!SteamAPI_Init())
            {
                return SteamRejected;
            }

            try
            {
                ready.Set();
                while (!stop.WaitOne(75))
                {
                    if (PlaytimeBudget.DeadlinePassed(deadline, deadlineTimestamp))
                    {
                        return SafetyStopped;
                    }

                    SteamAPI_RunCallbacks();
                }

                return 0;
            }
            finally
            {
                SteamAPI_Shutdown();
            }
        }
    }
}
