// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.DeviceAgent;

public sealed class WheelSessionManager
{
    private static readonly TimeSpan RescanDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NativeModeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CalibrationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultNativeModeSettleDelay = TimeSpan.FromMilliseconds(250);

    private readonly IHidDeviceEnumerator enumerator;
    private readonly IHidNotificationSource notifications;
    private readonly IHidTransportFactory transportFactory;
    private readonly IHidCalibrationMonitor calibrationMonitor;
    private readonly ILegacyBrokerControlClient broker;
    private readonly bool observeOnly;
    private readonly TimeSpan nativeModeSettleDelay;
    private readonly HashSet<string> attemptedConnections = new(StringComparer.OrdinalIgnoreCase);
    private string? attachedDevicePath;

    public WheelSessionManager(
        IHidDeviceEnumerator enumerator,
        IHidNotificationSource notifications,
        IHidTransportFactory transportFactory,
        IHidCalibrationMonitor calibrationMonitor,
        ILegacyBrokerControlClient broker,
        bool observeOnly,
        TimeSpan? nativeModeSettleDelay = null)
    {
        this.enumerator = enumerator;
        this.notifications = notifications;
        this.transportFactory = transportFactory;
        this.calibrationMonitor = calibrationMonitor;
        this.broker = broker;
        this.observeOnly = observeOnly;
        this.nativeModeSettleDelay = nativeModeSettleDelay ?? DefaultNativeModeSettleDelay;
    }

    public DeviceLifecycleState State { get; private set; } = DeviceLifecycleState.Absent;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        IAsyncEnumerator<HidDeviceChange> watcher =
            notifications.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Task<bool>? nextNotification = null;
        try
        {
            nextNotification = watcher.MoveNextAsync().AsTask();
            await ProcessCurrentDevicesAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await nextNotification.WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(RescanDebounce, cancellationToken).ConfigureAwait(false);
                do
                {
                    nextNotification = watcher.MoveNextAsync().AsTask();
                }
                while (nextNotification.IsCompletedSuccessfully && nextNotification.Result);

                await ProcessCurrentDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (nextNotification is not null)
            {
                try { await nextNotification.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            await watcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessCurrentDevicesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HidDeviceSnapshot> all = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        HidDeviceSnapshot[] logitechJoysticks = all
            .Where(device => device.VendorId == ClassicWheelCatalog.LogitechVendorId && device.IsJoystick)
            .ToArray();

        StructuredLog.Write("hid-scan", new
        {
            Count = logitechJoysticks.Length,
            Devices = logitechJoysticks.Select(DeviceDiagnostic),
        });

        HidDeviceSnapshot[] nativeDfgt = logitechJoysticks
            .Where(device =>
                device.ProductId == 0xC29A &&
                ClassicWheelCatalog.TryIdentify(
                    device.VendorId,
                    device.ProductId,
                    device.VersionNumber,
                    out WheelIdentity? identity) &&
                identity?.Definition.Model == WheelModel.DrivingForceGT)
            .ToArray();
        if (nativeDfgt.Length > 1)
        {
            SetState(DeviceLifecycleState.Faulted, "ambiguous-native-device-selection");
            StructuredLog.Write("switch-refused", new { Reason = "Multiple native DFGT collections were found." });
            return;
        }
        if (nativeDfgt.Length == 1)
        {
            if (State == DeviceLifecycleState.Attached &&
                string.Equals(attachedDevicePath, nativeDfgt[0].DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await AttachNativeDeviceAsync(
                nativeDfgt[0],
                "already-native",
                settleAfterNativeSwitch: false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        HidDeviceSnapshot[] compatibleDfgt = logitechJoysticks
            .Where(device =>
                device.ProductId == ClassicWheelCatalog.CompatibilityProductId &&
                ClassicWheelCatalog.TryIdentify(
                    device.VendorId,
                    device.ProductId,
                    device.VersionNumber,
                    out WheelIdentity? identity) &&
                identity?.Definition.Model == WheelModel.DrivingForceGT)
            .ToArray();

        if (compatibleDfgt.Length == 0)
        {
            if (logitechJoysticks.Any(device => device.ProductId == ClassicWheelCatalog.CompatibilityProductId))
            {
                StructuredLog.Write("switch-refused", new { Reason = "C294 revision is not a known DFGT identity." });
            }
            if (attachedDevicePath is not null)
            {
                try
                {
                    await broker.EmergencyStopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    StructuredLog.Error("broker-stop-after-removal-failed", exception);
                }
                try
                {
                    await broker.DetachAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    StructuredLog.Error("broker-detach-after-removal-failed", exception);
                }
                attachedDevicePath = null;
            }
            attemptedConnections.Clear();
            SetState(DeviceLifecycleState.Absent, "no-supported-dfgt");
            return;
        }

        if (compatibleDfgt.Length != 1 || nativeDfgt.Length > 1)
        {
            SetState(DeviceLifecycleState.Faulted, "ambiguous-device-selection");
            StructuredLog.Write("switch-refused", new { Reason = "Multiple matching DFGT collections were found." });
            return;
        }

        HidDeviceSnapshot source = compatibleDfgt[0];
        SetState(DeviceLifecycleState.Observed, "compatibility-device-observed");
        SetState(DeviceLifecycleState.Identified, "revision-identified-dfgt");
        if (observeOnly)
        {
            StructuredLog.Write("switch-skipped", new { Reason = "observe-only", Device = DeviceDiagnostic(source) });
            return;
        }

        string connectionKey = ConnectionKey(source);
        if (!attemptedConnections.Add(connectionKey))
        {
            StructuredLog.Write("switch-skipped", new { Reason = "already-attempted-this-connection", Connection = connectionKey });
            return;
        }

        await SwitchAndAttachAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task SwitchAndAttachAsync(
        HidDeviceSnapshot source,
        CancellationToken cancellationToken)
    {
        if (!ClassicWheelCatalog.TryIdentify(
                source.VendorId,
                source.ProductId,
                source.VersionNumber,
                out WheelIdentity? identity) ||
            identity is null)
        {
            StructuredLog.Write("switch-refused", new { Reason = "unknown-identity" });
            return;
        }

        IReadOnlyList<LogitechCommand> commands = identity.Definition.NativeModeSwitchSequence;
        if (commands.Count != 2)
        {
            throw new InvalidOperationException("The DFGT native-mode sequence must contain exactly two commands.");
        }

        SetState(DeviceLifecycleState.Calibrating, "waiting-for-power-on-calibration");
        try
        {
            SteeringCalibrationObservation calibration = await calibrationMonitor
                .WaitForCompletionAsync(source, CalibrationTimeout, cancellationToken)
                .ConfigureAwait(false);
            StructuredLog.Write("wheel-calibration-complete", new
            {
                Device = DeviceDiagnostic(source),
                calibration.SampleCount,
                calibration.Minimum,
                calibration.Maximum,
                calibration.Last,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(DeviceLifecycleState.Faulted, "power-on-calibration-failed");
            StructuredLog.Error("wheel-calibration-failed", exception, DeviceDiagnostic(source));
            return;
        }

        byte[][] reports = commands
            .Select(command => HidOutputReportFormatter.FormatUnnumberedCommand(command, source.OutputReportByteLength))
            .ToArray();
        SetState(DeviceLifecycleState.Switching, "sending-native-mode-sequence");
        StructuredLog.Write("mode-switch-report", new
        {
            Device = DeviceDiagnostic(source),
            Reports = reports.Select(Convert.ToHexString),
        });

        await using (IHidTransport transport = transportFactory.OpenForOutput(source))
        {
            await transport.SetOutputReportAsync(reports[0], cancellationToken).ConfigureAwait(false);
            try
            {
                await transport.SetOutputReportAsync(reports[1], cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception exception) when (IsExpectedDetachError(exception.NativeErrorCode))
            {
                StructuredLog.Write("mode-switch-detach-during-second-report", new { exception.NativeErrorCode });
            }
        }

        SetState(DeviceLifecycleState.AwaitingNativeMode, "waiting-for-c29a");
        DateTimeOffset deadline = DateTimeOffset.UtcNow + NativeModeTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(RescanDebounce, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<HidDeviceSnapshot> devices = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            HidDeviceSnapshot[] candidates = devices
                .Where(device => device.IsJoystick && device.VendorId == 0x046D && device.ProductId == 0xC29A)
                .Where(device => Correlates(source, device))
                .ToArray();
            if (candidates.Length == 1)
            {
                await AttachNativeDeviceAsync(
                    candidates[0],
                    CorrelationMethod(source, candidates[0]),
                    settleAfterNativeSwitch: true,
                    cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (candidates.Length > 1)
            {
                SetState(DeviceLifecycleState.Faulted, "ambiguous-native-mode-correlation");
                return;
            }
        }

        SetState(DeviceLifecycleState.Faulted, "native-mode-timeout");
        StructuredLog.Write("mode-switch-timeout", new { TimeoutSeconds = NativeModeTimeout.TotalSeconds });
    }

    private async Task AttachNativeDeviceAsync(
        HidDeviceSnapshot device,
        string correlation,
        bool settleAfterNativeSwitch,
        CancellationToken cancellationToken)
    {
        SetState(DeviceLifecycleState.NativeModeReady, correlation);
        try
        {
            if (settleAfterNativeSwitch)
            {
                await Task.Delay(nativeModeSettleDelay, cancellationToken).ConfigureAwait(false);
                StructuredLog.Write("native-mode-settled", new
                {
                    DelayMilliseconds = nativeModeSettleDelay.TotalMilliseconds,
                });
            }
            LegacyBrokerStatus attached = await broker.AttachAsync(device.DevicePath, cancellationToken).ConfigureAwait(false);
            LegacyBrokerStatus profiled = await broker.ApplyProfileAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!attached.Connected || !profiled.Connected)
            {
                throw new InvalidOperationException("Broker did not report an attached HID device.");
            }

            SetState(DeviceLifecycleState.Attached, "legacy-broker-ready");
            attachedDevicePath = device.DevicePath;
            StructuredLog.Write("broker-attached", new { Status = profiled, Device = DeviceDiagnostic(device) });
        }
        catch (Exception exception)
        {
            SetState(DeviceLifecycleState.Faulted, "broker-attach-failed");
            StructuredLog.Error("broker-attach-failed", exception, DeviceDiagnostic(device));
        }
    }

    private void SetState(DeviceLifecycleState state, string reason)
    {
        if (State == state)
        {
            return;
        }

        DeviceLifecycleState previous = State;
        State = state;
        StructuredLog.Write("state-transition", new { Previous = previous, Current = state, Reason = reason });
    }

    private static bool Correlates(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId ||
        source.EffectiveLocationPaths.Intersect(
            candidate.EffectiveLocationPaths,
            StringComparer.OrdinalIgnoreCase).Any();

    private static string CorrelationMethod(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId
            ? "container-id"
            : "location-path";

    private static string ConnectionKey(HidDeviceSnapshot device) =>
        device.ContainerId?.ToString("D") ??
        device.EffectiveLocationPaths.FirstOrDefault() ??
        device.InstanceId;

    private static bool IsExpectedDetachError(int error) =>
        error is 6 or 31 or 995 or 1167;

    private static object DeviceDiagnostic(HidDeviceSnapshot device) => new
    {
        Vid = $"{device.VendorId:X4}",
        Pid = $"{device.ProductId:X4}",
        Revision = $"{device.VersionNumber:X4}",
        Usage = $"{device.UsagePage:X4}:{device.Usage:X4}",
        device.InputReportByteLength,
        device.OutputReportByteLength,
        device.FeatureReportByteLength,
        device.InstanceId,
        device.ParentInstanceId,
        device.ContainerId,
        LocationPaths = device.EffectiveLocationPaths,
        device.BusReportedDescription,
        device.DriverService,
        device.DriverProvider,
        device.DriverInfPath,
        device.DevicePath,
    };
}
