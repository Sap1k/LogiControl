// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker;

public sealed record RuntimeProfileResult(
    int FrequencyHertz,
    double DurationSeconds,
    long ExpectedTicks,
    long Ticks,
    double EffectiveTickRateHertz,
    long MissedDeadlines,
    long Overruns,
    long MixerAllocatedBytes,
    long TicksWithAllocations,
    long AbsoluteJitterP99Microseconds,
    long AbsoluteJitterP999Microseconds,
    long MixerComputationP99Microseconds,
    long CommandToMixP99Microseconds,
    long Commands,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    bool MeetsProvisionalGoals);

public static class RuntimeProfileRunner
{
    public static async Task<RuntimeProfileResult> RunAsync(
        TimeSpan duration,
        bool stress,
        bool profileEvents,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var clock = new QpcMonotonicClock();
        var engine = new EffectEngine(clock);
        using var runtime = new EffectRuntime(engine, clock, new NullForceFeedbackOutputSink(), profileEvents);
        runtime.Start();
        for (int i = 0; i < EffectDefinitionValidator.MaximumEffectsPerSession; i++)
        {
            var common = new EffectCommon(
                EffectCommon.InfiniteDuration,
                0,
                0,
                10_000,
                i % 2 == 0 ? 10_000 : -10_000,
                null);
            var effect = new PeriodicEffectDefinition(
                common,
                (ForceEffectKind)((int)ForceEffectKind.Square + i % 5),
                500,
                0,
                (uint)(i * 2_250),
                (uint)(20_000 + i * 1_000));
            uint handle = runtime.Invoke(() =>
            {
                EngineResult result = engine.Upsert(0, effect, false, out uint assigned);
                if (result != EngineResult.Ok)
                {
                    throw new InvalidOperationException($"Unable to prepare profile effect: {result}.");
                }

                return assigned;
            }, TimeSpan.FromSeconds(1));
            runtime.Invoke(() =>
            {
                if (engine.Start(handle) != EngineResult.Ok)
                {
                    throw new InvalidOperationException("Unable to start profile effect.");
                }
            }, TimeSpan.FromSeconds(1));
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        TelemetryBaseline baseline = Capture(runtime.Telemetry);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        using var stressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] stressTasks = stress ? StartStress(stressCancellation.Token) : [];
        long started = clock.GetMicroseconds();
        Task mutations = ApplyMutationsAsync(runtime, engine, duration, cancellationToken);
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        await mutations.ConfigureAwait(false);
        long ended = clock.GetMicroseconds();
        stressCancellation.Cancel();
        try
        {
            await Task.WhenAll(stressTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stressCancellation.IsCancellationRequested)
        {
        }

        TelemetryBaseline after = Capture(runtime.Telemetry);
        double seconds = (ended - started) / 1_000_000.0;
        long ticks = after.Ticks - baseline.Ticks;
        long missed = after.MissedDeadlines - baseline.MissedDeadlines;
        long overruns = after.Overruns - baseline.Overruns;
        long allocations = after.MixerAllocatedBytes - baseline.MixerAllocatedBytes;
        long allocationTicks = after.TicksWithAllocations - baseline.TicksWithAllocations;
        long commands = after.CommandCount - baseline.CommandCount;
        long[] jitter = Difference(after.AbsoluteJitterBuckets, baseline.AbsoluteJitterBuckets);
        long[] computation = Difference(after.ComputationBuckets, baseline.ComputationBuckets);
        long[] command = Difference(after.CommandToMixBuckets, baseline.CommandToMixBuckets);
        long jitterP99 = PercentileUpperBound(jitter, 0.99);
        long jitterP999 = PercentileUpperBound(jitter, 0.999);
        long computationP99 = PercentileUpperBound(computation, 0.99);
        long commandP99 = PercentileUpperBound(command, 0.99);
        bool goals = computationP99 <= 250 && jitterP99 <= 500 && jitterP999 <= 2_000 &&
            commandP99 <= 2_000 && allocations == 0;
        return new RuntimeProfileResult(
            EffectRuntime.FrequencyHertz,
            seconds,
            (long)Math.Round(seconds * EffectRuntime.FrequencyHertz),
            ticks,
            ticks / seconds,
            missed,
            overruns,
            allocations,
            allocationTicks,
            jitterP99,
            jitterP999,
            computationP99,
            commandP99,
            commands,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            goals);
    }

    private static async Task ApplyMutationsAsync(
        EffectRuntime runtime,
        EffectEngine engine,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        int gain = 10_000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            gain = gain == 10_000 ? 9_999 : 10_000;
            int next = gain;
            runtime.Invoke(() =>
            {
                if (engine.SetGameGain(next) != EngineResult.Ok)
                {
                    throw new InvalidOperationException("Unable to mutate profile gain.");
                }
            }, TimeSpan.FromSeconds(1));
        }
    }

    private static Task[] StartStress(CancellationToken cancellationToken)
    {
        int workers = Math.Max(1, Environment.ProcessorCount / 2);
        var tasks = new Task[workers + 1];
        for (int i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                double value = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    for (int iteration = 1; iteration <= 10_000; iteration++)
                    {
                        value += Math.Sqrt(iteration + value % 17);
                    }
                }

                GC.KeepAlive(value);
            }, cancellationToken);
        }

        tasks[^1] = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[][] pressure = Enumerable.Range(0, 128).Select(static _ => new byte[16_384]).ToArray();
                GC.KeepAlive(pressure);
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
        return tasks;
    }

    private static TelemetryBaseline Capture(RuntimeTelemetry telemetry) => new(
        telemetry.Ticks,
        telemetry.MissedDeadlines,
        telemetry.Overruns,
        telemetry.CommandCount,
        telemetry.MixerAllocatedBytes,
        telemetry.TicksWithAllocations,
        telemetry.AbsoluteJitterBuckets.ToArray(),
        telemetry.ComputationBuckets.ToArray(),
        telemetry.CommandToMixBuckets.ToArray());

    private static long[] Difference(long[] after, long[] before)
    {
        var result = new long[after.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = after[i] - before[i];
        }

        return result;
    }

    private static long PercentileUpperBound(long[] buckets, double percentile)
    {
        long total = buckets.Sum();
        if (total == 0)
        {
            return 0;
        }

        long target = (long)Math.Ceiling(total * percentile);
        long cumulative = 0;
        ReadOnlySpan<long> bounds = RuntimeTelemetry.BucketBoundsMicroseconds;
        for (int i = 0; i < buckets.Length; i++)
        {
            cumulative += buckets[i];
            if (cumulative >= target)
            {
                return i < bounds.Length ? bounds[i] : long.MaxValue;
            }
        }

        return long.MaxValue;
    }

    private sealed record TelemetryBaseline(
        long Ticks,
        long MissedDeadlines,
        long Overruns,
        long CommandCount,
        long MixerAllocatedBytes,
        long TicksWithAllocations,
        long[] AbsoluteJitterBuckets,
        long[] ComputationBuckets,
        long[] CommandToMixBuckets);
}
