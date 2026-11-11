using System;
using System.Diagnostics;
using System.Threading;

namespace MMOPlugin
{
    /// <summary>
    /// Low-overhead timing counters for locating periodic SimHub update stalls.
    /// The counters are emitted as one log entry every 30 seconds.
    /// </summary>
    internal static class PluginPerformanceDiagnostics
    {
        private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;
        private static long _nextReportTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30L;
        private static int _lastGen0 = GC.CollectionCount(0);
        private static int _lastGen1 = GC.CollectionCount(1);
        private static int _lastGen2 = GC.CollectionCount(2);
        private static long _skippedStandingsApiPolls;
        private static long _nextTrackLimitLogTimestamp;
        private static volatile bool _enabled;

        internal static bool Enabled { get { return _enabled; } }

        internal static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
                return;

            _enabled = false;
            DataUpdate.Reset();
            PropertyGetter.Reset();
            TelemetryScan.Reset();
            LiveCalculations.Reset();
            StandingsApiPoll.Reset();
            VehicleMetadataPoll.Reset();
            VehicleMetadataSync.Reset();
            StatisticsFileWrite.Reset();
            Interlocked.Exchange(ref _skippedStandingsApiPolls, 0);
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            Interlocked.Exchange(
                ref _nextReportTimestamp,
                Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30L
            );
            Interlocked.Exchange(
                ref _nextTrackLimitLogTimestamp,
                Stopwatch.GetTimestamp()
            );
            _enabled = enabled;
        }

        internal static readonly DurationMetric DataUpdate = new DurationMetric();
        internal static readonly DurationMetric PropertyGetter = new DurationMetric();
        internal static readonly DurationMetric TelemetryScan = new DurationMetric();
        internal static readonly DurationMetric LiveCalculations = new DurationMetric();
        internal static readonly DurationMetric StandingsApiPoll = new DurationMetric();
        internal static readonly DurationMetric VehicleMetadataPoll = new DurationMetric();
        internal static readonly DurationMetric VehicleMetadataSync = new DurationMetric();
        internal static readonly DurationMetric StatisticsFileWrite = new DurationMetric();

        internal static void TryLogPeriodicSummary()
        {
            if (!Enabled)
                return;

            long now = Stopwatch.GetTimestamp();
            long next = Interlocked.Read(ref _nextReportTimestamp);
            if (now < next || Interlocked.CompareExchange(
                    ref _nextReportTimestamp,
                    now + Stopwatch.Frequency * 30L,
                    next) != next)
                return;

            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            string report = "[MMOPlugin performance, 30s] " +
                "DataUpdate=" + DataUpdate.TakeSummary() +
                "; PropertyGetter(sample 1/128, estimated count)=" + PropertyGetter.TakeSummary() +
                "; TelemetryScan=" + TelemetryScan.TakeSummary() +
                "; LiveCalculations=" + LiveCalculations.TakeSummary() +
                "; StandingsApi=" + StandingsApiPoll.TakeSummary() +
                "; VehicleMetadata=" + VehicleMetadataPoll.TakeSummary() +
                "; MetadataSync=" + VehicleMetadataSync.TakeSummary() +
                "; StatisticsWrite=" + StatisticsFileWrite.TakeSummary() +
                "; StandingsPollsSkipped=" + Interlocked.Exchange(ref _skippedStandingsApiPolls, 0) +
                "; GC collections gen0/1/2=" + (gen0 - _lastGen0) + "/" +
                (gen1 - _lastGen1) + "/" + (gen2 - _lastGen2);

            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            // Logging is deliberately kept off the SimHub data-update thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!Enabled)
                    return;
                try { global::SimHub.Logging.Current.Info(report); }
                catch { /* Diagnostics must never interrupt telemetry updates. */ }
            });
        }

        internal static void RecordSkippedStandingsApiPoll()
        {
            if (Enabled)
                Interlocked.Increment(ref _skippedStandingsApiPolls);
        }

        internal static void TryLogTrackLimitSample(
            int telemetryIndex,
            bool headerValid,
            bool playerHasVehicle,
            bool slotDataValid,
            int telemetryId,
            int scoringPlayerId,
            int rawSteps,
            int stepsPerPoint,
            int stepsPerPenalty,
            double currentPoints,
            double limitPoints)
        {
            if (!Enabled)
                return;

            long now = Stopwatch.GetTimestamp();
            long next = Interlocked.Read(ref _nextTrackLimitLogTimestamp);
            if (now < next || Interlocked.CompareExchange(
                    ref _nextTrackLimitLogTimestamp,
                    now + Stopwatch.Frequency,
                    next) != next)
                return;

            string report =
                "index=" + telemetryIndex +
                "; headerValid=" + headerValid +
                "; playerHasVehicle=" + playerHasVehicle +
                "; slotDataValid=" + slotDataValid +
                "; telemetryId=" + telemetryId +
                "; scoringPlayerId=" + scoringPlayerId +
                "; playerIdMatches=" + (slotDataValid && telemetryId == scoringPlayerId) +
                "; rawSteps=" + rawSteps +
                "; stepsPerPoint=" + stepsPerPoint +
                "; stepsPerPenalty=" + stepsPerPenalty +
                "; current=" + currentPoints.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                "; limit=" + limitPoints.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            // Keep diagnostic I/O off the live telemetry update thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!Enabled)
                    return;
                try { global::SimHub.Logging.Current.Info("[MMOPlugin track limits] " + report); }
                catch { /* Diagnostics must never interrupt telemetry updates. */ }
            });
        }

        internal sealed class DurationMetric
        {
            private long _count;
            private long _totalTicks;
            private long _maxTicks;
            private long _totalAllocatedBytes;
            private long _maxAllocatedBytes;
            private long _over8Ms;
            private long _over16Ms;
            private long _over50Ms;
            private long _over250Ms;

            internal void Reset()
            {
                Interlocked.Exchange(ref _count, 0);
                Interlocked.Exchange(ref _totalTicks, 0);
                Interlocked.Exchange(ref _maxTicks, 0);
                Interlocked.Exchange(ref _totalAllocatedBytes, 0);
                Interlocked.Exchange(ref _maxAllocatedBytes, 0);
                Interlocked.Exchange(ref _over8Ms, 0);
                Interlocked.Exchange(ref _over16Ms, 0);
                Interlocked.Exchange(ref _over50Ms, 0);
                Interlocked.Exchange(ref _over250Ms, 0);
            }

            internal void Add(long startedAt)
            {
                Add(startedAt, -1L);
            }

            internal void Add(long startedAt, long allocatedBytesAtStart)
            {
                Add(startedAt, allocatedBytesAtStart, 1L);
            }

            internal void AddSampled(long startedAt, long allocatedBytesAtStart, int sampleWeight)
            {
                Add(startedAt, allocatedBytesAtStart, sampleWeight);
            }

            private void Add(long startedAt, long allocatedBytesAtStart, long sampleWeight)
            {
                if (!Enabled)
                    return;

                long elapsed = Stopwatch.GetTimestamp() - startedAt;
                Interlocked.Add(ref _count, sampleWeight);
                Interlocked.Add(ref _totalTicks, elapsed * sampleWeight);
                UpdateMaximum(ref _maxTicks, elapsed);
                if (allocatedBytesAtStart >= 0)
                {
                    long allocatedBytes = Math.Max(
                        0L,
                        GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart
                    );
                    Interlocked.Add(ref _totalAllocatedBytes, allocatedBytes * sampleWeight);
                    UpdateMaximum(ref _maxAllocatedBytes, allocatedBytes);
                }
                if (elapsed >= Stopwatch.Frequency * 8L / 1000L)
                    Interlocked.Add(ref _over8Ms, sampleWeight);
                if (elapsed >= Stopwatch.Frequency * 16L / 1000L)
                    Interlocked.Add(ref _over16Ms, sampleWeight);
                if (elapsed >= Stopwatch.Frequency * 50L / 1000L)
                    Interlocked.Add(ref _over50Ms, sampleWeight);
                if (elapsed >= Stopwatch.Frequency / 4L)
                    Interlocked.Add(ref _over250Ms, sampleWeight);
            }

            internal string TakeSummary()
            {
                long count = Interlocked.Exchange(ref _count, 0);
                long total = Interlocked.Exchange(ref _totalTicks, 0);
                long maximum = Interlocked.Exchange(ref _maxTicks, 0);
                long allocated = Interlocked.Exchange(ref _totalAllocatedBytes, 0);
                long maxAllocated = Interlocked.Exchange(ref _maxAllocatedBytes, 0);
                long over8 = Interlocked.Exchange(ref _over8Ms, 0);
                long over16 = Interlocked.Exchange(ref _over16Ms, 0);
                long over50 = Interlocked.Exchange(ref _over50Ms, 0);
                long over250 = Interlocked.Exchange(ref _over250Ms, 0);
                double averageMs = count > 0 ? total * TicksToMilliseconds / count : 0.0;
                double maximumMs = maximum * TicksToMilliseconds;
                return count.ToString() + " calls avg/max=" +
                    averageMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "/" +
                    maximumMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "ms alloc avg/max=" + (count > 0 ? allocated / count : 0L) + "/" + maxAllocated +
                    "B >8/16/50/250ms=" + over8 + "/" + over16 + "/" + over50 + "/" + over250;
            }

            private static void UpdateMaximum(ref long target, long value)
            {
                long current = Interlocked.Read(ref target);
                while (value > current)
                {
                    long observed = Interlocked.CompareExchange(ref target, value, current);
                    if (observed == current) return;
                    current = observed;
                }
            }
        }
    }
}
