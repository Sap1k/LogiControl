// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public enum BrokerDeviceLifecycleState
{
    Absent,
    Observed,
    Identified,
    Calibrating,
    Switching,
    AwaitingNativeMode,
    NativeModeReady,
    Attached,
    Faulted,
}

public sealed record BrokerDeviceManagerOptions(
    TimeSpan RescanDebounce,
    TimeSpan NativeModeTimeout,
    TimeSpan CalibrationTimeout,
    TimeSpan NativeModeSettleDelay,
    TimeSpan OutputReadyTimeout)
{
    public static BrokerDeviceManagerOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1));
}

public sealed class BrokerDeviceManager
{
    private readonly IHidDeviceEnumerator enumerator;
    private readonly IHidNotificationSource notifications;
    private readonly IHidTransportFactory transportFactory;
    private readonly IHidCalibrationMonitor calibrationMonitor;
    private readonly BrokerSessionCoordinator coordinator;
    private readonly EffectRuntime runtime;
    private readonly SwitchableForceFeedbackOutputSink output;
    private readonly BrokerDeviceManagerOptions options;
    private readonly bool profileEvents;
    private readonly HashSet<string> attemptedConnections = new(StringComparer.OrdinalIgnoreCase);
    private string? attachedDevicePath;
    private int deviceReady;

    public BrokerDeviceManager(
        IHidDeviceEnumerator enumerator,
        IHidNotificationSource notifications,
        IHidTransportFactory transportFactory,
        IHidCalibrationMonitor calibrationMonitor,
        BrokerSessionCoordinator coordinator,
        EffectRuntime runtime,
        SwitchableForceFeedbackOutputSink output,
        BrokerDeviceManagerOptions? options = null,
        bool profileEvents = false)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.calibrationMonitor = calibrationMonitor ?? throw new ArgumentNullException(nameof(calibrationMonitor));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.options = options ?? BrokerDeviceManagerOptions.Default;
        this.profileEvents = profileEvents;
    }

    public BrokerDeviceLifecycleState State { get; private set; } = BrokerDeviceLifecycleState.Absent;

    public string LastTransitionReason { get; private set; } = "initial";

    public bool IsDeviceReady => Volatile.Read(ref deviceReady) != 0 && output.IsAttached;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<HidDeviceChange> watcher =
            notifications.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Task<bool> notification = watcher.MoveNextAsync().AsTask();
        Task<CoalescingHidOutputPump> deviceFault = output.WaitForDeviceFaultAsync(cancellationToken).AsTask();
        try
        {
            await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(notification, deviceFault)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (completed == deviceFault)
                {
                    CoalescingHidOutputPump failedPump = await deviceFault.ConfigureAwait(false);
                    await HandleOutputFaultAsync(failedPump).ConfigureAwait(false);
                    deviceFault = output.WaitForDeviceFaultAsync(cancellationToken).AsTask();
                }
                else
                {
                    if (!await notification.ConfigureAwait(false))
                    {
                        break;
                    }

                    notification = watcher.MoveNextAsync().AsTask();
                }

                await Task.Delay(options.RescanDebounce, cancellationToken).ConfigureAwait(false);
                await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await DetachCurrentAsync("broker-shutdown").ConfigureAwait(false);
        }
    }

    public async Task ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HidDeviceSnapshot> all = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        HidDeviceSnapshot[] logitechJoysticks = all
            .Where(static device => device.VendorId == ClassicWheelCatalog.LogitechVendorId && device.IsJoystick)
            .ToArray();
        HidDeviceSnapshot[] nativeDfgt = logitechJoysticks.Where(IsNativeDfgt).ToArray();
        if (nativeDfgt.Length > 1)
        {
            await FaultAndDetachAsync("ambiguous-native-device-selection").ConfigureAwait(false);
            return;
        }

        if (nativeDfgt.Length == 1)
        {
            HidDeviceSnapshot native = nativeDfgt[0];
            if (IsDeviceReady && string.Equals(attachedDevicePath, native.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await AttachNativeDeviceAsync(native, "already-native", false, cancellationToken).ConfigureAwait(false);
            return;
        }

        HidDeviceSnapshot[] compatibleDfgt = logitechJoysticks.Where(IsCompatibleDfgt).ToArray();
        if (compatibleDfgt.Length == 0)
        {
            await DetachCurrentAsync("device-absent").ConfigureAwait(false);
            attemptedConnections.Clear();
            SetState(BrokerDeviceLifecycleState.Absent, "no-supported-dfgt");
            return;
        }

        if (compatibleDfgt.Length != 1)
        {
            await FaultAndDetachAsync("ambiguous-compatibility-device-selection").ConfigureAwait(false);
            return;
        }

        HidDeviceSnapshot source = compatibleDfgt[0];
        SetState(BrokerDeviceLifecycleState.Observed, "compatibility-device-observed");
        SetState(BrokerDeviceLifecycleState.Identified, "revision-identified-dfgt");
        string connectionKey = ConnectionKey(source);
        if (!attemptedConnections.Add(connectionKey))
        {
            return;
        }

        await SwitchAndAttachAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task SwitchAndAttachAsync(HidDeviceSnapshot source, CancellationToken cancellationToken)
    {
        if (!ClassicWheelCatalog.TryIdentify(
                source.VendorId, source.ProductId, source.VersionNumber, out WheelIdentity? identity) || identity is null)
        {
            return;
        }

        IReadOnlyList<LogitechCommand> commands = identity.Definition.NativeModeSwitchSequence;
        if (commands.Count != 2)
        {
            throw new InvalidOperationException("The DFGT native-mode sequence must contain exactly two commands.");
        }

        SetState(BrokerDeviceLifecycleState.Calibrating, "waiting-for-power-on-calibration");
        try
        {
            _ = await calibrationMonitor.WaitForCompletionAsync(
                source, options.CalibrationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(BrokerDeviceLifecycleState.Faulted, $"power-on-calibration-failed:{exception.GetType().Name}");
            return;
        }

        byte[][] reports = commands
            .Select(command => HidOutputReportFormatter.FormatUnnumberedCommand(command, source.OutputReportByteLength))
            .ToArray();
        SetState(BrokerDeviceLifecycleState.Switching, "sending-native-mode-sequence");
        await using (IHidTransport transport = transportFactory.OpenForOutput(source))
        {
            await transport.SetOutputReportAsync(reports[0], cancellationToken).ConfigureAwait(false);
            try
            {
                await transport.SetOutputReportAsync(reports[1], cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception exception) when (IsExpectedDetachError(exception.NativeErrorCode))
            {
            }
        }

        SetState(BrokerDeviceLifecycleState.AwaitingNativeMode, "waiting-for-c29a");
        long started = Environment.TickCount64;
        while (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) < options.NativeModeTimeout)
        {
            await Task.Delay(options.RescanDebounce, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<HidDeviceSnapshot> devices = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            HidDeviceSnapshot[] candidates = devices
                .Where(IsNativeDfgt)
                .Where(device => Correlates(source, device))
                .ToArray();
            if (candidates.Length == 1)
            {
                await AttachNativeDeviceAsync(
                    candidates[0], CorrelationMethod(source, candidates[0]), true, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (candidates.Length > 1)
            {
                SetState(BrokerDeviceLifecycleState.Faulted, "ambiguous-native-mode-correlation");
                return;
            }
        }

        SetState(BrokerDeviceLifecycleState.Faulted, "native-mode-timeout");
    }

    private async Task AttachNativeDeviceAsync(
        HidDeviceSnapshot device,
        string correlation,
        bool settleAfterNativeSwitch,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref deviceReady, 0);
        SetState(BrokerDeviceLifecycleState.NativeModeReady, correlation);
        if (device.OutputReportByteLength != DfgtForceFeedbackReports.ReportLength)
        {
            SetState(BrokerDeviceLifecycleState.Faulted, "unexpected-native-output-report-length");
            return;
        }

        if (settleAfterNativeSwitch)
        {
            await Task.Delay(options.NativeModeSettleDelay, cancellationToken).ConfigureAwait(false);
        }

        await DetachCurrentAsync("replace-device").ConfigureAwait(false);
        IHidTransport? transport = null;
        CoalescingHidOutputPump? pump = null;
        try
        {
            transport = transportFactory.OpenForOutput(device);
            await InitializeNativeOutputAsync(transport, cancellationToken).ConfigureAwait(false);
            pump = new CoalescingHidOutputPump(transport, profileEvents: profileEvents);
            transport = null;
            runtime.Invoke(() =>
            {
                output.Attach(pump);
                runtime.ResetOutputPolicyForAttach();
            }, options.OutputReadyTimeout);

            RuntimeSettings settings = coordinator.RuntimeSettings;
            var finalAutocenter = new byte[DfgtForceFeedbackReports.ReportLength];
            if (settings.IdleAutocenter > 0)
            {
                DfgtForceFeedbackReports.WriteEnableAutocenter(finalAutocenter);
            }
            else
            {
                DfgtForceFeedbackReports.WriteDisableAutocenter(finalAutocenter);
            }

            using var readyTimeout = new CancellationTokenSource(options.OutputReadyTimeout);
            using var readyLinked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, readyTimeout.Token);
            await pump.PublishBarrierAndWaitAsync(finalAutocenter, readyLinked.Token).ConfigureAwait(false);
            attachedDevicePath = device.DevicePath;
            Volatile.Write(ref deviceReady, 1);
            SetState(BrokerDeviceLifecycleState.Attached, "managed-broker-output-ready");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref deviceReady, 0);
            CoalescingHidOutputPump? attached = runtime.Invoke(() => output.Detach(), options.OutputReadyTimeout);
            (attached ?? pump)?.Dispose();
            SetState(BrokerDeviceLifecycleState.Faulted, $"broker-attach-failed:{exception.GetType().Name}");
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task InitializeNativeOutputAsync(IHidTransport transport, CancellationToken cancellationToken)
    {
        RuntimeSettings settings = coordinator.RuntimeSettings;
        var report = new byte[DfgtForceFeedbackReports.ReportLength];
        DfgtForceFeedbackReports.WriteStopAll(report);
        await transport.WriteOutputReportAsync(report, cancellationToken).ConfigureAwait(false);
        DfgtForceFeedbackReports.WriteDisableAutocenter(report);
        await transport.WriteOutputReportAsync(report, cancellationToken).ConfigureAwait(false);
        DfgtForceFeedbackReports.WriteRange(report, settings.RangeDegrees);
        await transport.WriteOutputReportAsync(report, cancellationToken).ConfigureAwait(false);
        DfgtForceFeedbackReports.WriteAutocenterParameters(report, settings.IdleAutocenter);
        await transport.WriteOutputReportAsync(report, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleOutputFaultAsync(CoalescingHidOutputPump failedPump)
    {
        Volatile.Write(ref deviceReady, 0);
        attachedDevicePath = null;
        try
        {
            runtime.Invoke(coordinator.DeviceRemoved, options.OutputReadyTimeout);
        }
        catch (TimeoutException)
        {
        }

        failedPump.Dispose();
        SetState(BrokerDeviceLifecycleState.Faulted, "continuous-output-fault");
        await Task.CompletedTask;
    }

    private async Task DetachCurrentAsync(string reason)
    {
        Volatile.Write(ref deviceReady, 0);
        if (!output.IsAttached)
        {
            attachedDevicePath = null;
            return;
        }

        try
        {
            runtime.Invoke(coordinator.DeviceRemoved, options.OutputReadyTimeout);
        }
        catch (TimeoutException)
        {
        }

        CoalescingHidOutputPump? pump;
        try
        {
            pump = runtime.Invoke(() => output.Detach(), options.OutputReadyTimeout);
        }
        catch (TimeoutException)
        {
            pump = output.Detach();
        }

        if (pump is not null)
        {
            var stopAll = new byte[DfgtForceFeedbackReports.ReportLength];
            DfgtForceFeedbackReports.WriteStopAll(stopAll);
            try
            {
                using var timeout = new CancellationTokenSource(options.OutputReadyTimeout);
                await pump.PublishBarrierAndWaitAsync(stopAll, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
            }
            finally
            {
                pump.Dispose();
            }
        }

        attachedDevicePath = null;
        LastTransitionReason = reason;
    }

    private async Task FaultAndDetachAsync(string reason)
    {
        await DetachCurrentAsync(reason).ConfigureAwait(false);
        SetState(BrokerDeviceLifecycleState.Faulted, reason);
    }

    private void SetState(BrokerDeviceLifecycleState state, string reason)
    {
        BrokerDeviceLifecycleState previous = State;
        State = state;
        LastTransitionReason = reason;
        if (previous != state)
        {
            BrokerEventSource.Log.DeviceLifecycle(previous.ToString(), state.ToString(), reason);
        }
    }

    private static bool IsNativeDfgt(HidDeviceSnapshot device) =>
        device.ProductId == 0xC29A &&
        ClassicWheelCatalog.TryIdentify(device.VendorId, device.ProductId, device.VersionNumber, out WheelIdentity? identity) &&
        identity?.Definition.Model == WheelModel.DrivingForceGT;

    private static bool IsCompatibleDfgt(HidDeviceSnapshot device) =>
        device.ProductId == ClassicWheelCatalog.CompatibilityProductId &&
        ClassicWheelCatalog.TryIdentify(device.VendorId, device.ProductId, device.VersionNumber, out WheelIdentity? identity) &&
        identity?.Definition.Model == WheelModel.DrivingForceGT;

    private static bool Correlates(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId ||
        source.EffectiveLocationPaths.Intersect(candidate.EffectiveLocationPaths, StringComparer.OrdinalIgnoreCase).Any();

    private static string CorrelationMethod(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId ? "container-id" : "location-path";

    private static string ConnectionKey(HidDeviceSnapshot device) =>
        device.ContainerId?.ToString("D") ?? device.EffectiveLocationPaths.FirstOrDefault() ?? device.InstanceId;

    private static bool IsExpectedDetachError(int error) => error is 6 or 31 or 995 or 1167;
}
