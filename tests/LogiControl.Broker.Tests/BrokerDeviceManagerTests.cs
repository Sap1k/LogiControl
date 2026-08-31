// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LogiControl.Hid;

namespace LogiControl.Broker.Tests;

public sealed class BrokerDeviceManagerTests
{
    [Fact]
    public async Task AlreadyNativeDeviceIsInitializedAndMadeReadyBeforeBinding()
    {
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", Guid.NewGuid());
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[native]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Attached, fixture.Manager.State);
        CapturingTransport transport = Assert.Single(factory.Transports);
        byte[][] reports = transport.WriteReports.ToArray();
        Assert.True(reports.Length >= 5);
        Assert.Equal((byte)0xF3, reports[0][1]);
        Assert.Equal((byte)0xF5, reports[1][1]);
        Assert.Equal((byte)0xF8, reports[2][1]);
        Assert.Equal((byte)0xFE, reports[3][1]);
        Assert.Empty(transport.SetReports);
    }

    [Fact]
    public async Task CompatibilityModeCalibratesSwitchesCorrelatesAndAttaches()
    {
        Guid container = Guid.NewGuid();
        HidDeviceSnapshot compatible = Snapshot(0xC294, "compatibility", container);
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", container);
        var calibration = new CapturingCalibrationMonitor();
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([[compatible], [native]]), factory, calibration);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, calibration.WaitCount);
        Assert.Same(compatible, calibration.Device);
        Assert.True(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Attached, fixture.Manager.State);
        Assert.Equal(2, factory.Transports.Count);
        Assert.Equal(new byte[] { 0, 0xF8, 0x0A, 0, 0, 0, 0, 0 }, factory.Transports[0].SetReports[0]);
        Assert.Equal(new byte[] { 0, 0xF8, 0x09, 0x03, 0x01, 0, 0, 0 }, factory.Transports[0].SetReports[1]);
        Assert.Empty(factory.Transports[1].SetReports);
    }

    [Fact]
    public async Task UnknownCompatibilityRevisionIsStrictlyReadOnly()
    {
        HidDeviceSnapshot unknown = Snapshot(0xC294, "unknown", Guid.NewGuid()) with { VersionNumber = 0x2000 };
        var calibration = new CapturingCalibrationMonitor();
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[unknown]]), factory, calibration);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Absent, fixture.Manager.State);
        Assert.Equal(0, calibration.WaitCount);
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task DuplicateScanDoesNotReopenAttachedDevice()
    {
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", Guid.NewGuid());
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[native], [native]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(factory.Transports);
        Assert.True(fixture.Manager.IsDeviceReady);
    }

    [Fact]
    public async Task RemovalInvalidatesReadinessAndCompletesStopAllBeforeClose()
    {
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", Guid.NewGuid());
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[native], []]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        CapturingTransport transport = Assert.Single(factory.Transports);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Absent, fixture.Manager.State);
        Assert.Equal((byte)0xF3, transport.WriteReports.Last()[1]);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task CalibrationFailureSendsNoModeOrForceReports()
    {
        HidDeviceSnapshot compatible = Snapshot(0xC294, "compatibility", Guid.NewGuid());
        var calibration = new CapturingCalibrationMonitor { Failure = new TimeoutException("injected") };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([[compatible]]), factory, calibration);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BrokerDeviceLifecycleState.Faulted, fixture.Manager.State);
        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Empty(factory.Transports);
    }

    private static HidDeviceSnapshot Snapshot(ushort productId, string suffix, Guid container) => new(
        $"path-{suffix}",
        $"HID\\VID_046D&PID_{productId:X4}\\{suffix}",
        0x046D,
        productId,
        0x1326,
        0x01,
        0x04,
        8,
        8,
        9,
        container,
        ["PCIROOT(0)#USBROOT(0)#USB(2)"],
        $"USB\\VID_046D&PID_{productId:X4}\\{suffix}");

    private sealed class DeviceFixture : IDisposable
    {
        private readonly SwitchableForceFeedbackOutputSink output = new();
        private readonly EffectRuntime runtime;

        public DeviceFixture(
            IHidDeviceEnumerator enumerator,
            IHidTransportFactory factory,
            IHidCalibrationMonitor? calibration = null)
        {
            var clock = new QpcMonotonicClock();
            var coordinator = new BrokerSessionCoordinator(clock);
            runtime = new EffectRuntime(coordinator, clock, output);
            runtime.Start();
            Manager = new BrokerDeviceManager(
                enumerator,
                new SilentNotifications(),
                factory,
                calibration ?? new CapturingCalibrationMonitor(),
                coordinator,
                runtime,
                output,
                new BrokerDeviceManagerOptions(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1)));
        }

        public BrokerDeviceManager Manager { get; }

        public void Dispose() => runtime.Dispose();
    }

    private sealed class SequenceEnumerator(IReadOnlyList<IReadOnlyList<HidDeviceSnapshot>> values)
        : IHidDeviceEnumerator
    {
        private int index;

        public ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int selected = Math.Min(Interlocked.Increment(ref index) - 1, values.Count - 1);
            return ValueTask.FromResult(values[selected]);
        }
    }

    private sealed class SilentNotifications : IHidNotificationSource
    {
        public async IAsyncEnumerable<HidDeviceChange> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class CapturingCalibrationMonitor : IHidCalibrationMonitor
    {
        public int WaitCount { get; private set; }
        public HidDeviceSnapshot? Device { get; private set; }
        public Exception? Failure { get; init; }

        public ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
            HidDeviceSnapshot device,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            Device = device;
            return Failure is null
                ? ValueTask.FromResult(new SteeringCalibrationObservation(4, 0, 16_383, 8_192))
                : ValueTask.FromException<SteeringCalibrationObservation>(Failure);
        }
    }

    private sealed class CapturingTransportFactory : IHidTransportFactory
    {
        public List<CapturingTransport> Transports { get; } = [];

        public IHidTransport OpenForOutput(HidDeviceSnapshot device)
        {
            var transport = new CapturingTransport(device);
            Transports.Add(transport);
            return transport;
        }
    }

    private sealed class CapturingTransport(HidDeviceSnapshot device) : IHidTransport
    {
        private int disposeCount;

        public HidDeviceSnapshot Device { get; } = device;
        public List<byte[]> SetReports { get; } = [];
        public ConcurrentQueue<byte[]> WriteReports { get; } = new();
        public int DisposeCount => Volatile.Read(ref disposeCount);

        public ValueTask SetOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetReports.Add(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteReports.Enqueue(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
