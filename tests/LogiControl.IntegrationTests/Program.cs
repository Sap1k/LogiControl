// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using LogiControl.DeviceAgent;
using LogiControl.Hid;

var failures = new List<string>();
await Run("Mode-switch decisions are identity-gated", TestDecision, failures);
await Run("C294 switches once, correlates C29A, and attaches", TestSwitchAndAttach, failures);
await Run("Removal detaches after failed emergency stop and reattaches", TestReconnectAfterFailedStop, failures);

foreach (string failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static Task TestDecision()
{
    ModeSwitchDecision known = ModeSwitchDecision.Evaluate(0x046D, 0xC294, 0x1301);
    ModeSwitchDecision unknown = ModeSwitchDecision.Evaluate(0x046D, 0xC294, 0x2000);
    ModeSwitchDecision native = ModeSwitchDecision.Evaluate(0x046D, 0xC29A, 0x1301);

    Require(known.IsAllowed && known.Commands.Count == 2, "Known DFGT did not produce a bounded switch decision.");
    Require(!unknown.IsAllowed && unknown.Commands.Count == 0, "Unknown C294 revision produced output.");
    Require(!native.IsAllowed && native.Commands.Count == 0, "Native DFGT was switched again.");
    return Task.CompletedTask;
}

static async Task TestSwitchAndAttach()
{
    Guid container = Guid.NewGuid();
    HidDeviceSnapshot c294 = Snapshot(0xC294, "compatibility", container);
    HidDeviceSnapshot c29a = Snapshot(0xC29A, "native", container);
    var enumerator = new SequenceEnumerator([[c294], [c29a]]);
    var transport = new CapturingTransport(c294);
    var factory = new CapturingTransportFactory(transport);
    var calibration = new FakeCalibrationMonitor();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var broker = new FakeBroker(() => cancellation.Cancel());
    var manager = new WheelSessionManager(
        enumerator,
        new SilentNotifications(),
        factory,
        calibration,
        broker,
        observeOnly: false,
        nativeModeSettleDelay: TimeSpan.Zero);

    await manager.RunAsync(cancellation.Token);

    Require(manager.State == DeviceLifecycleState.Attached, $"Unexpected final state {manager.State}.");
    Require(transport.Reports.Count == 2, "Expected exactly two SetOutputReport calls.");
    Require(transport.Reports[0].SequenceEqual(new byte[] { 0, 0xF8, 0x0A, 0, 0, 0, 0, 0 }), "First native report differs.");
    Require(transport.Reports[1].SequenceEqual(new byte[] { 0, 0xF8, 0x09, 0x03, 0x01, 0, 0, 0 }), "Second native report differs.");
    Require(broker.AttachedPath == c29a.DevicePath, "Broker did not receive the correlated native path.");
    Require(broker.ProfileApplied, "Default profile was not applied.");
    Require(calibration.WaitCount == 1, "Switched native mode did not wait for calibration.");
    Require(calibration.Device == c294, "Calibration did not observe the compatibility-mode device.");
    Require(broker.ProfileApplyCount == 1, "Profile was not applied exactly once after calibration.");
}

static async Task TestReconnectAfterFailedStop()
{
    Guid container = Guid.NewGuid();
    HidDeviceSnapshot native = Snapshot(0xC29A, "native", container);
    var enumerator = new SequenceEnumerator([[native], [], [native]]);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var broker = new ReconnectBroker(() => cancellation.Cancel());
    var manager = new WheelSessionManager(
        enumerator,
        new TimedNotifications(),
        new CapturingTransportFactory(new CapturingTransport(native)),
        new FakeCalibrationMonitor(),
        broker,
        observeOnly: false);

    await manager.RunAsync(cancellation.Token);

    Require(broker.EmergencyStopCount == 1, "Removal did not attempt EmergencyStop exactly once.");
    Require(broker.DetachCount == 1, "Detach was skipped after EmergencyStop failed.");
    Require(broker.AttachCount == 2, "Native device was not attached again after reconnect.");
    Require(broker.ProfileCount == 2, "Default profile was not reapplied after reconnect.");
    Require(manager.State == DeviceLifecycleState.Attached, $"Unexpected reconnect state {manager.State}.");
}

static HidDeviceSnapshot Snapshot(ushort productId, string suffix, Guid container) => new(
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
    $"USB\\VID_046D&PID_{productId:X4}\\{suffix}",
    "Driving Force GT",
    "HidUsb",
    "Microsoft",
    "input.inf");

static async Task Run(string name, Func<Task> test, ICollection<string> failures)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class SequenceEnumerator(IReadOnlyList<IReadOnlyList<HidDeviceSnapshot>> values) : IHidDeviceEnumerator
{
    private int index;

    public ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int selected = Math.Min(index++, values.Count - 1);
        return ValueTask.FromResult(values[selected]);
    }
}

file sealed class SilentNotifications : IHidNotificationSource
{
    public async IAsyncEnumerable<HidDeviceChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }
}

file sealed class TimedNotifications : IHidNotificationSource
{
    public async IAsyncEnumerable<HidDeviceChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken);
        yield return HidDeviceChange.RescanRequired;
        await Task.Delay(300, cancellationToken);
        yield return HidDeviceChange.RescanRequired;
    }
}

file sealed class CapturingTransportFactory(CapturingTransport transport) : IHidTransportFactory
{
    public IHidTransport OpenForOutput(HidDeviceSnapshot device)
    {
        Require(device == transport.Device, "Unexpected transport device.");
        return transport;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

file sealed class CapturingTransport(HidDeviceSnapshot device) : IHidTransport
{
    public HidDeviceSnapshot Device { get; } = device;

    public List<byte[]> Reports { get; } = [];

    public ValueTask SetOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reports.Add(report.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutputReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Mode switching must not use WriteFile output.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeCalibrationMonitor : IHidCalibrationMonitor
{
    public int WaitCount { get; private set; }

    public HidDeviceSnapshot? Device { get; private set; }

    public ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
        HidDeviceSnapshot device,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ++WaitCount;
        Device = device;
        return ValueTask.FromResult(new SteeringCalibrationObservation(5, 0, 16383, 8192));
    }
}

file sealed class FakeBroker(Action profileApplied) : ILegacyBrokerControlClient
{
    public string? AttachedPath { get; private set; }

    public bool ProfileApplied { get; private set; }

    public int ProfileApplyCount { get; private set; }

    public ValueTask<LegacyBrokerStatus> AttachAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        AttachedPath = devicePath;
        return ValueTask.FromResult(Status());
    }

    public ValueTask<LegacyBrokerStatus> ApplyProfileAsync(
        int rangeDegrees = 900,
        int overallGain = 10000,
        int boundaryForce = 3000,
        CancellationToken cancellationToken = default)
    {
        ProfileApplied = true;
        ++ProfileApplyCount;
        profileApplied();
        return ValueTask.FromResult(Status());
    }

    public ValueTask<LegacyBrokerStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Status());

    public ValueTask<LegacyBrokerStatus> EmergencyStopAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Status());

    public ValueTask<LegacyBrokerStatus> DetachAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Status());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static LegacyBrokerStatus Status() =>
        new(LegacyBrokerDeviceState.ProfileActive, true, false, true, 0, 900, 10000, 3000, 0, "desktop");
}

file sealed class ReconnectBroker(Action reattached) : ILegacyBrokerControlClient
{
    public int AttachCount { get; private set; }
    public int ProfileCount { get; private set; }
    public int EmergencyStopCount { get; private set; }
    public int DetachCount { get; private set; }

    public ValueTask<LegacyBrokerStatus> AttachAsync(
        string devicePath,
        CancellationToken cancellationToken = default)
    {
        ++AttachCount;
        return ValueTask.FromResult(Status());
    }

    public ValueTask<LegacyBrokerStatus> ApplyProfileAsync(
        int rangeDegrees = 900,
        int overallGain = 10000,
        int boundaryForce = 3000,
        CancellationToken cancellationToken = default)
    {
        ++ProfileCount;
        if (ProfileCount == 2) reattached();
        return ValueTask.FromResult(Status());
    }

    public ValueTask<LegacyBrokerStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Status());

    public ValueTask<LegacyBrokerStatus> EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        ++EmergencyStopCount;
        throw new InvalidOperationException("Legacy broker command failed with HRESULT 0x8007048F.");
    }

    public ValueTask<LegacyBrokerStatus> DetachAsync(CancellationToken cancellationToken = default)
    {
        ++DetachCount;
        return ValueTask.FromResult(Status());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static LegacyBrokerStatus Status() =>
        new(LegacyBrokerDeviceState.ProfileActive, true, false, true, 0, 900, 10000, 3000, 0, "desktop");
}
