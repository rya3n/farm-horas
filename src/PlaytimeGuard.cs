using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FarmHoras
{
    internal enum PlaytimeStage
    {
        Normal,
        FineAdjustment,
        WaitingForSteam,
        InsufficientTime,
        Interrupted
    }

    internal struct PlaytimePlan
    {
        internal readonly PlaytimeStage Stage;
        internal readonly long EstimateSeconds;
        internal readonly long SessionSeconds;

        internal PlaytimePlan(PlaytimeStage stage, long estimateSeconds, long sessionSeconds = 0)
        {
            Stage = stage;
            EstimateSeconds = estimateSeconds;
            SessionSeconds = sessionSeconds;
        }
    }

    internal sealed class PlaytimeGuard
    {
        internal const int MarginMinutes = 30;
        internal const int SafeLimitSeconds = SteamPlaytime.TargetSeconds - MarginMinutes * 60;
        internal const int FineLimitSeconds = SteamPlaytime.TargetSeconds - 60;
        internal const int ShutdownSeconds = 5;
        private const int MinimumFineSeconds = 70;
        private const int MaximumFineSeconds = 120;
        private const int UnconfirmedExit = -1;
        private readonly string path;
        private Dictionary<ulong, GuardRecord> recorded;

        private struct GuardRecord
        {
            internal readonly long EstimateSeconds;
            internal readonly long RequiredSteamMinutes;

            internal GuardRecord(long estimateSeconds, long requiredSteamMinutes)
            {
                EstimateSeconds = estimateSeconds;
                RequiredSteamMinutes = requiredSteamMinutes;
            }
        }

        internal PlaytimeGuard(string directory)
        {
            path = Path.Combine(directory, "playtime-guard.txt");
        }

        internal PlaytimeBudget Reserve(uint account, uint appId, int steamMinutes, bool fineAdjustment, out PlaytimeStage stage)
        {
            if (account == 0 || steamMinutes < 0)
            {
                throw new ArgumentException("Conta ou total da Steam inválido.");
            }

            ulong key = Key(account, appId);
            GuardRecord prior = GetRecord(key);
            PlaytimePlan plan = Plan(prior, steamMinutes);
            stage = plan.Stage;
            if (stage != (fineAdjustment ? PlaytimeStage.FineAdjustment : PlaytimeStage.Normal))
            {
                if (stage != PlaytimeStage.Interrupted && plan.EstimateSeconds > prior.EstimateSeconds)
                {
                    Save(key, new GuardRecord(plan.EstimateSeconds, prior.RequiredSteamMinutes));
                }
                return null;
            }

            var budget = new PlaytimeBudget(account, key, plan.EstimateSeconds, plan.SessionSeconds,
                fineAdjustment, steamMinutes, prior.RequiredSteamMinutes);
            // Retain the full reservation if the manager crashes before observing the worker exit.
            Save(key, new GuardRecord(plan.EstimateSeconds + plan.SessionSeconds + ShutdownSeconds, UnconfirmedExit));
            return budget;
        }

        internal bool TryGetPlan(uint account, uint appId, int steamMinutes, out PlaytimePlan plan)
        {
            plan = default(PlaytimePlan);
            try
            {
                plan = Plan(GetRecord(Key(account, appId)), steamMinutes);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static long RemainingSeconds(int limitSeconds, long estimate, int shutdownSeconds = 0)
        {
            return Math.Max(0, limitSeconds - estimate - shutdownSeconds);
        }

        private static ulong Key(uint account, uint appId)
        {
            return ((ulong)account << 32) | appId;
        }

        private GuardRecord GetRecord(ulong key)
        {
            GuardRecord record;
            Load();
            recorded.TryGetValue(key, out record);
            return record;
        }

        private static PlaytimePlan Plan(GuardRecord record, int steamMinutes)
        {
            // The minute reported by Steam may already be partly elapsed.
            long estimate = Math.Max(record.EstimateSeconds, (long)steamMinutes * 60 + 60);
            if (record.RequiredSteamMinutes == UnconfirmedExit)
            {
                return new PlaytimePlan(PlaytimeStage.Interrupted, estimate);
            }

            long remaining = RemainingSeconds(SafeLimitSeconds, estimate, ShutdownSeconds);
            if (remaining > 0)
            {
                return new PlaytimePlan(PlaytimeStage.Normal, estimate, remaining);
            }

            remaining = RemainingSeconds(FineLimitSeconds, estimate, ShutdownSeconds);
            if (remaining < MinimumFineSeconds)
            {
                return new PlaytimePlan(PlaytimeStage.InsufficientTime, estimate);
            }

            if (steamMinutes < record.RequiredSteamMinutes)
            {
                return new PlaytimePlan(PlaytimeStage.WaitingForSteam, estimate);
            }

            return new PlaytimePlan(PlaytimeStage.FineAdjustment, estimate, Math.Min(remaining, MaximumFineSeconds));
        }

        internal void Settle(PlaytimeBudget budget, bool workerMayHavePlayed)
        {
            long estimate = workerMayHavePlayed ? budget.EstimatedTotalSeconds : budget.BaselineSeconds;
            long required = budget.PreviousSteamRequirement;
            if (workerMayHavePlayed)
            {
                // Ask for a new reading. The estimate still includes all locally measured time.
                required = Math.Max(required, (long)budget.StartSteamMinutes + 1);
            }

            Save(budget.Key, new GuardRecord(estimate, required));
        }

        private static long MinutesExpectedFromEstimate(long estimate)
        {
            // Allow for the initial partial minute and time reserved for shutdown.
            return Math.Max(0, (estimate - 60 - ShutdownSeconds) / 60);
        }

        private void Load()
        {
            if (recorded != null)
            {
                return;
            }

            var values = new Dictionary<ulong, GuardRecord>();
            if (File.Exists(path))
            {
                foreach (string line in File.ReadLines(path))
                {
                    string[] parts = line.Split(' ');
                    ulong key;
                    long total;
                    long required = 0;
                    if ((parts.Length != 2 && parts.Length != 3) ||
                        !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out key) || (key >> 32) == 0 || (uint)key == 0 ||
                        !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out total) || values.ContainsKey(key) ||
                        (parts.Length == 3 && (!long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out required) || required < UnconfirmedExit)))
                    {
                        throw new InvalidDataException("O arquivo de proteção de horas está inválido.");
                    }

                    values.Add(key, new GuardRecord(total, parts.Length == 2 ? MinutesExpectedFromEstimate(total) : required));
                }
            }
            recorded = values;
        }

        private void Save(ulong key, GuardRecord record)
        {
            var updated = new Dictionary<ulong, GuardRecord>(recorded);
            updated[key] = record;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                {
                    foreach (var pair in updated)
                    {
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
                            pair.Key, pair.Value.EstimateSeconds, pair.Value.RequiredSteamMinutes));
                    }
                }
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }

            recorded = updated;
        }
    }

    internal sealed class PlaytimeBudget
    {
        private readonly long startedTimestamp = Stopwatch.GetTimestamp();
        private readonly DateTime started = DateTime.UtcNow;
        private readonly long seconds;
        private long adjustedBaseline;
        private long greatestElapsed;

        internal uint AccountId { get; private set; }
        internal ulong Key { get; private set; }
        internal bool IsFineAdjustment { get; private set; }
        internal int StartSteamMinutes { get; private set; }
        internal long PreviousSteamRequirement { get; private set; }
        internal long BaselineSeconds { get; private set; }
        internal int LimitSeconds { get { return IsFineAdjustment ? PlaytimeGuard.FineLimitSeconds : PlaytimeGuard.SafeLimitSeconds; } }
        internal DateTime DeadlineUtc { get { return started.AddSeconds(seconds); } }
        internal long DeadlineTimestamp { get { return startedTimestamp + seconds * Stopwatch.Frequency; } }
        internal long SecondsUntilDeadline
        {
            get
            {
                double monotonic = (DeadlineTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                return Math.Max(0, (long)Math.Floor(Math.Min(monotonic, (DeadlineUtc - DateTime.UtcNow).TotalSeconds)));
            }
        }
        internal long EstimatedTotalSeconds
        {
            get { return adjustedBaseline + ReadElapsedSeconds() + PlaytimeGuard.ShutdownSeconds; }
        }
        internal bool Expired { get { return DeadlinePassed(DeadlineUtc, DeadlineTimestamp); } }

        internal static bool DeadlinePassed(DateTime utc, long timestamp, int graceSeconds = 0)
        {
            return Stopwatch.GetTimestamp() - timestamp >= Stopwatch.Frequency * graceSeconds ||
                DateTime.UtcNow - utc >= TimeSpan.FromSeconds(graceSeconds);
        }

        internal bool Observe(int steamMinutes)
        {
            long elapsed = ReadElapsedSeconds();
            long reported = (long)steamMinutes * 60;
            bool exceedsEstimate = reported > adjustedBaseline + elapsed + PlaytimeGuard.ShutdownSeconds;
            // Keep the newer bound and continue counting time from the point it was observed.
            adjustedBaseline = Math.Max(adjustedBaseline, reported + 60 - elapsed);
            return exceedsEstimate;
        }

        private long ReadElapsedSeconds()
        {
            double monotonic = (Stopwatch.GetTimestamp() - startedTimestamp) / (double)Stopwatch.Frequency;
            long elapsed = (long)Math.Ceiling(Math.Max(monotonic, (DateTime.UtcNow - started).TotalSeconds));
            greatestElapsed = Math.Max(greatestElapsed, elapsed);
            return greatestElapsed;
        }

        internal PlaytimeBudget(uint accountId, ulong key, long baseline, long seconds,
            bool fineAdjustment, int startSteamMinutes, long previousSteamRequirement)
        {
            AccountId = accountId;
            Key = key;
            BaselineSeconds = baseline;
            adjustedBaseline = baseline;
            this.seconds = seconds;
            IsFineAdjustment = fineAdjustment;
            StartSteamMinutes = startSteamMinutes;
            PreviousSteamRequirement = previousSteamRequirement;
        }
    }
}
