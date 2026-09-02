// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void RuntimeSustainsFiveHundredHertzAndAcknowledgesCommandsImmediately()
    {
        var clock = new QpcMonotonicClock();
        var engine = new EffectEngine(clock);
        var output = new CapturingOutputSink();
        using var runtime = new EffectRuntime(engine, clock, output);
        runtime.Start();

        uint handle = runtime.Invoke(() =>
        {
            Assert.Equal(EngineResult.Ok, engine.Upsert(0,
                new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 2_000), false, out uint assigned));
            return assigned;
        }, TimeSpan.FromSeconds(1));
        Assert.Equal(EngineResult.Ok, runtime.Invoke(() => engine.Start(handle), TimeSpan.FromSeconds(1)));

        Assert.True(SpinWait.SpinUntil(() => runtime.Telemetry.Ticks >= 15, TimeSpan.FromSeconds(2)));
        runtime.Dispose();
        Assert.InRange(runtime.Telemetry.Ticks, 15, 1_000);
        Assert.Contains(2_000, output.Forces);
        Assert.True(runtime.Telemetry.CommandCount >= 2);
        Assert.Equal(runtime.Telemetry.Ticks, runtime.Telemetry.AbsoluteJitterBuckets.Span.ToArray().Sum());
        Assert.Equal(runtime.Telemetry.Ticks,
            runtime.Telemetry.EarlyJitterBuckets.Span.ToArray().Sum() +
            runtime.Telemetry.LateJitterBuckets.Span.ToArray().Sum());
        Assert.Equal(runtime.Telemetry.Ticks, runtime.Telemetry.ComputationBuckets.Span.ToArray().Sum());
        Assert.Equal(runtime.Telemetry.CommandCount, runtime.Telemetry.CommandToMixBuckets.Span.ToArray().Sum());
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    public void HidPumpCoalescesSlotZeroWhileAWriteIsPending(double completionMilliseconds)
    {
        var transport = new DelayedTransport(TimeSpan.FromMilliseconds(completionMilliseconds));
        using (var pump = new CoalescingHidOutputPump(transport))
        {
            for (int i = 0; i <= 100; i++)
            {
                pump.PublishSoftwareForce(i * 100);
            }

            Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count > 0, TimeSpan.FromSeconds(2)));
            Thread.Sleep(50);
            Assert.True(pump.CoalescedReports > 0);
            Assert.Equal(101, pump.Telemetry.DesiredPublications);
            Assert.Equal(pump.CoalescedReports, pump.Telemetry.CoalescedReports);
            Assert.True(pump.Telemetry.HidSubmissions >= pump.Telemetry.HidCompletions);
            Assert.True(pump.Telemetry.HidCompletions > 0);
            Assert.Equal(pump.Telemetry.HidCompletions,
                pump.Telemetry.HidDurationBuckets.Span.ToArray().Sum());
            Assert.Equal(pump.Telemetry.HidCompletions,
                pump.Telemetry.PublicationToCompletionBuckets.Span.ToArray().Sum());
        }

        Assert.InRange(transport.Reports.Count, 1, 100);
        Assert.Equal((byte)0xFF, transport.Reports.Last()[3]);
    }

    [Fact]
    public void RuntimeDisablesAutocenterForEffectsAndRestoresConfiguredIdleStrengthAfterFinalStop()
    {
        var clock = new QpcMonotonicClock();
        var engine = new EffectEngine(clock);
        var output = new CapturingOutputSink();
        using var runtime = new EffectRuntime(engine, clock, output);
        runtime.Start();
        RuntimeSettings settings = RuntimeSettings.Default with { IdleAutocenter = 3_000 };
        runtime.Invoke(() => Assert.Equal(EngineResult.Ok, engine.SetRuntimeSettings(settings)), TimeSpan.FromSeconds(1));
        uint handle = runtime.Invoke(() =>
        {
            Assert.Equal(EngineResult.Ok, engine.Upsert(0,
                new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 1_000), false, out uint assigned));
            return assigned;
        }, TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(
            () => output.Barriers.Any(static report => report[1] == 0x14), TimeSpan.FromSeconds(1)));
        output.Barriers.Clear();

        runtime.Invoke(() => Assert.Equal(EngineResult.Ok, engine.Start(handle)), TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(
            () => output.Barriers.Any(static report => report[1] == 0xF5), TimeSpan.FromSeconds(1)));
        output.Barriers.Clear();

        runtime.Invoke(() => Assert.Equal(EngineResult.Ok, engine.Stop(handle)), TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(
            () => output.Barriers.Any(static report => report[1] == 0x14), TimeSpan.FromSeconds(1)));
        byte[][] restored = output.Barriers.ToArray();
        Assert.Contains(restored, static report => report[1] == 0x13);
        Assert.Contains(restored, static report => report[1] == 0xFE && report[2] == 0x0D);
        Assert.Contains(restored, static report => report[1] == 0x14);
        int stopIndex = Array.FindIndex(restored, static report => report[1] == 0x13);
        int parametersIndex = Array.FindIndex(restored, static report => report[1] == 0xFE && report[2] == 0x0D);
        int enableIndex = Array.FindIndex(restored, static report => report[1] == 0x14);
        Assert.True(stopIndex < parametersIndex);
        Assert.True(parametersIndex < enableIndex);
    }

    [Fact]
    public void HidPumpStartsThenUpdatesSlotZeroAndSkipsInitialZero()
    {
        var transport = new DelayedTransport(TimeSpan.Zero);
        using var pump = new CoalescingHidOutputPump(transport);

        pump.PublishSoftwareForce(0);
        pump.PublishSoftwareForce(1_000);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 1, TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0x11, transport.Reports.ElementAt(0)[1]);

        pump.PublishSoftwareForce(2_000);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 2, TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0x1C, transport.Reports.ElementAt(1)[1]);

        pump.PublishSoftwareForce(0);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 3, TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0x1C, transport.Reports.ElementAt(2)[1]);
        Assert.Equal((byte)0x80, transport.Reports.ElementAt(2)[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HidPumpResetBarrierMakesNextForceStart(bool stopAll)
    {
        var protocol = DfgtProtocol();
        var transport = new DelayedTransport(TimeSpan.Zero);
        using var pump = new CoalescingHidOutputPump(transport);
        pump.PublishSoftwareForce(1_000);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 1, TimeSpan.FromSeconds(2)));

        var reset = new byte[protocol.ReportLength];
        if (stopAll)
        {
            protocol.WriteStopAll(reset);
        }
        else
        {
            protocol.WriteSlotStop(reset, 0);
        }

        await pump.PublishBarrierAndWaitAsync(reset, TestContext.Current.CancellationToken);
        pump.PublishSoftwareForce(2_000);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 3, TimeSpan.FromSeconds(2)));

        Assert.Equal((byte)0x11, transport.Reports.ElementAt(0)[1]);
        Assert.Equal(stopAll ? (byte)0xF3 : (byte)0x13, transport.Reports.ElementAt(1)[1]);
        Assert.Equal((byte)0x11, transport.Reports.ElementAt(2)[1]);
    }

    [Fact]
    public async Task StopAllFenceSuppressesOlderCoalescedForceButAllowsNewStart()
    {
        var protocol = DfgtProtocol();
        var transport = new GatedTransport();
        using var pump = new CoalescingHidOutputPump(transport);
        pump.PublishSoftwareForce(1_000);
        Assert.True(transport.FirstWriteStarted.Wait(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        pump.PublishSoftwareForce(2_000);
        var stopAll = new byte[protocol.ReportLength];
        protocol.WriteStopAll(stopAll);
        Task barrier = pump.PublishBarrierAndWaitAsync(
            stopAll, TestContext.Current.CancellationToken).AsTask();
        pump.PublishSoftwareForce(0);
        transport.ReleaseFirstWrite.Set();

        await barrier.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 2, TimeSpan.FromSeconds(2)));
        Thread.Sleep(25);
        Assert.Equal(2, transport.Reports.Count);
        Assert.Equal((byte)0x11, transport.Reports.ElementAt(0)[1]);
        Assert.Equal((byte)0xF3, transport.Reports.ElementAt(1)[1]);

        pump.PublishSoftwareForce(3_000);
        Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 3, TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0x11, transport.Reports.ElementAt(2)[1]);
    }

    [Fact]
    public void HidPumpPreservesBarrierOrder()
    {
        var protocol = DfgtProtocol();
        var transport = new DelayedTransport(TimeSpan.Zero);
        using (var pump = new CoalescingHidOutputPump(transport))
        {
            byte[] range = Assert.Single(protocol.CreateRangeReports(540));
            Span<byte> stop = stackalloc byte[protocol.ReportLength];
            protocol.WriteStopAll(stop);
            pump.PublishBarrier(range);
            pump.PublishBarrier(stop);
            Assert.True(SpinWait.SpinUntil(() => transport.Reports.Count >= 2, TimeSpan.FromSeconds(2)));
        }

        Assert.Equal((byte)0xF8, transport.Reports.ElementAt(0)[1]);
        Assert.Equal((byte)0xF3, transport.Reports.ElementAt(1)[1]);
    }

    private static ClassicWheelProtocol DfgtProtocol() =>
        new(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT));

    [Fact]
    public void HidWriteFailureAttemptsStopAllClosesTransportAndSignalsFault()
    {
        var transport = new FailingTransport();
        using var pump = new CoalescingHidOutputPump(transport);
        pump.PublishSoftwareForce(1_000);

        Assert.True(pump.Faulted.WaitOne(TimeSpan.FromSeconds(2)));
        Assert.NotNull(pump.Failure);
        Assert.True(transport.WriteAttempts >= 2);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, pump.Telemetry.WriteFailures);
    }

    [Fact]
    public async Task SwitchableOutputDetachesFailedPumpAndSignalsRuntimeAndLifecycle()
    {
        using var output = new SwitchableForceFeedbackOutputSink();
        var pump = new CoalescingHidOutputPump(new FailingTransport());
        output.Attach(pump);
        output.PublishSoftwareForce(1_000);

        CoalescingHidOutputPump failed = await output.WaitForDeviceFaultAsync(
            TestContext.Current.CancellationToken).AsTask().WaitAsync(
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Same(pump, failed);
        Assert.False(output.IsAttached);
        Assert.True(output.Faulted.WaitOne(TimeSpan.FromSeconds(2)));
        Assert.NotNull(output.Telemetry);
        Assert.Equal(1, output.Telemetry!.WriteFailures);
        failed.Dispose();
    }

    private static EffectCommon Common(uint duration) => new(duration, 0, 0, 10_000, 10_000, null);

    private sealed class CapturingOutputSink : IForceFeedbackOutputSink
    {
        private readonly ManualResetEvent faulted = new(false);

        public ConcurrentBag<int> Forces { get; } = [];

        public ConcurrentQueue<byte[]> Barriers { get; } = new();

        public WaitHandle Faulted => faulted;

        public void PublishSoftwareForce(int force) => Forces.Add(force);

        public void PublishConditionChange(ConditionSlotChange change)
        {
        }

        public void PublishBarrier(ReadOnlySpan<byte> report) => Barriers.Enqueue(report.ToArray());

        public void Dispose() => faulted.Dispose();
    }

    private sealed class DelayedTransport(TimeSpan delay) : IHidTransport
    {
        public HidDeviceSnapshot Device { get; } = new(
            "fake", "fake", 0x046D, 0xC29A, 0x1301, 1, 4, 16, 8, 8);

        public ConcurrentQueue<byte[]> Reports { get; } = new();

        public ValueTask SetOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Continuous output must not use HidD_SetOutputReport.");

        public async ValueTask WriteOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Reports.Enqueue(report.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingTransport : IHidTransport
    {
        public HidDeviceSnapshot Device { get; } = new(
            "fake", "fake", 0x046D, 0xC29A, 0x1301, 1, 4, 16, 8, 8);

        public int WriteAttempts { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask SetOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected mode-switch output.");

        public ValueTask WriteOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            throw new IOException("Injected HID write failure.");
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedTransport : IHidTransport
    {
        private int writes;

        public HidDeviceSnapshot Device { get; } = new(
            "fake", "fake", 0x046D, 0xC29A, 0x1301, 1, 4, 16, 8, 8);

        public ConcurrentQueue<byte[]> Reports { get; } = new();

        public ManualResetEventSlim FirstWriteStarted { get; } = new(false);

        public ManualResetEventSlim ReleaseFirstWrite { get; } = new(false);

        public ValueTask SetOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected mode-switch output.");

        public ValueTask WriteOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                FirstWriteStarted.Set();
                ReleaseFirstWrite.Wait(cancellationToken);
            }

            Reports.Enqueue(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            ReleaseFirstWrite.Set();
            FirstWriteStarted.Dispose();
            ReleaseFirstWrite.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
