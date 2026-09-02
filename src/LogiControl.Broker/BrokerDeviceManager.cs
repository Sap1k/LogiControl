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
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object candidateGate = new();
    private readonly HashSet<WheelDeviceId> attemptedConnections = [];
    private readonly Dictionary<WheelDeviceId, PhysicalWheelRecord> physicalWheels = [];
    private readonly Dictionary<WheelDeviceId, RuntimeSettings> settingsByIdentity = [];
    private DiscoveredCandidate[] candidates = [];
    private ulong nextDeviceId = 1;
    private long selectionRevision;
    private CancellationTokenSource? activeTransition;
    private WheelDeviceId pinnedDeviceId;
    private WheelDeviceId selectedDeviceId;
    private WheelDeviceId attachedDeviceId;
    private CoalescingHidOutputPump? attachedPump;
    private ClassicWheelProtocol activeProtocol = ClassicWheelProtocol.Default;
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

    public bool IsAutomaticSelection
    {
        get
        {
            lock (candidateGate)
            {
                return pinnedDeviceId == WheelDeviceId.Automatic;
            }
        }
    }

    public IReadOnlyList<BrokerWheelCandidate> GetCandidates()
    {
        lock (candidateGate)
        {
            return candidates.Select(candidate => new BrokerWheelCandidate(
                candidate.Id,
                candidate.Identity.Definition.Model,
                candidate.Identity.Definition.DisplayName,
                candidate.Device.VersionNumber,
                candidate.Device.ProductId,
                candidate.Device.DevicePath,
                candidate.Id == selectedDeviceId ? State : BrokerDeviceLifecycleState.Observed,
                candidate.Id == selectedDeviceId,
                candidate.Id == selectedDeviceId && IsDeviceReady)).ToArray();
        }
    }

    public bool CanBindPath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath) || !IsDeviceReady)
        {
            return false;
        }

        lock (candidateGate)
        {
            return selectedDeviceId != WheelDeviceId.Automatic &&
                string.Equals(attachedDevicePath, devicePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<bool> SelectAsync(
        WheelDeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        bool changesSelection;
        CancellationTokenSource? superseded = null;
        lock (candidateGate)
        {
            if (deviceId != WheelDeviceId.Automatic && candidates.All(candidate => candidate.Id != deviceId))
            {
                return false;
            }

            changesSelection = pinnedDeviceId != deviceId ||
                deviceId != WheelDeviceId.Automatic && selectedDeviceId != deviceId;
            pinnedDeviceId = deviceId;
            if (changesSelection)
            {
                selectionRevision++;
                Volatile.Write(ref deviceReady, 0);
                superseded = activeTransition;
            }
        }

        superseded?.Cancel();
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (changesSelection)
            {
                await DetachCurrentAsync("selection-changed").ConfigureAwait(false);
            }

            await RunScanTransitionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scanGate.Release();
        }

        return true;
    }

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
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunScanTransitionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scanGate.Release();
        }
    }

    public BrokerResult SetRuntimeSettings(RuntimeSettings settings)
    {
        if (!EffectDefinitionValidator.TryValidate(settings, out _))
        {
            return BrokerResult.InvalidArgument;
        }

        lock (candidateGate)
        {
            DiscoveredCandidate? selected = candidates.FirstOrDefault(candidate => candidate.Id == selectedDeviceId);
            if (selected is not null && !selected.Identity.Definition.SteeringRange.Supports(settings.RangeDegrees))
            {
                return BrokerResult.InvalidArgument;
            }

            BrokerResult result = coordinator.SetRuntimeSettings(settings);
            if (result != BrokerResult.Ok)
            {
                return result;
            }

            if (selected is not null)
            {
                settingsByIdentity[selected.Id] = settings;
                if (selected.Id == attachedDeviceId && IsDeviceReady)
                {
                    foreach (byte[] report in activeProtocol.CreateRangeReports(settings.RangeDegrees))
                    {
                        output.PublishBarrier(report);
                    }
                }
            }

            return BrokerResult.Ok;
        }
    }

    private async Task RunScanTransitionAsync(CancellationToken cancellationToken)
    {
        using TransitionLease transition = BeginTransition(cancellationToken);
        try
        {
            await ScanOnceCoreAsync(transition).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (transition.IsSuperseded)
        {
        }
        finally
        {
            lock (candidateGate)
            {
                if (ReferenceEquals(activeTransition, transition.Source))
                {
                    activeTransition = null;
                }
            }
        }
    }

    private TransitionLease BeginTransition(CancellationToken cancellationToken)
    {
        lock (candidateGate)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeTransition = source;
            return new TransitionLease(this, selectionRevision, source);
        }
    }

    private async Task ScanOnceCoreAsync(TransitionLease transition)
    {
        transition.ThrowIfInvalid();
        IReadOnlyList<HidDeviceSnapshot> all = await enumerator.EnumerateAsync(transition.Token).ConfigureAwait(false);
        transition.ThrowIfInvalid();
        HidDeviceSnapshot[] logitechJoysticks = all
            .Where(static device => device.VendorId == ClassicWheelCatalog.LogitechVendorId && device.IsJoystick)
            .ToArray();
        LogUnknownCompatibilityPresentations(logitechJoysticks);
        DiscoveredCandidate[] discovered = CreatePhysicalCandidates(logitechJoysticks);
        WheelDeviceId pin;
        lock (candidateGate)
        {
            candidates = discovered;
            attemptedConnections.IntersectWith(discovered.Select(static candidate => candidate.Id));
            pin = pinnedDeviceId;
        }

        DiscoveredCandidate? selected = pin == WheelDeviceId.Automatic
            ? discovered.Length == 1 ? discovered[0] : null
            : discovered.FirstOrDefault(candidate => candidate.Id == pin);
        if (selected is null)
        {
            lock (candidateGate)
            {
                selectedDeviceId = pin == WheelDeviceId.Automatic ? WheelDeviceId.Automatic : pin;
            }

            await DetachCurrentAsync(discovered.Length > 1 && pin == WheelDeviceId.Automatic
                ? "selection-required"
                : pin != WheelDeviceId.Automatic ? "selected-device-absent" : "device-absent").ConfigureAwait(false);
            if (discovered.Length == 0 || pin != WheelDeviceId.Automatic)
            {
                if (discovered.Length == 0)
                {
                    attemptedConnections.Clear();
                }

                SetState(BrokerDeviceLifecycleState.Absent,
                    pin == WheelDeviceId.Automatic ? "no-supported-wheel" : "selected-device-absent");
            }
            else
            {
                SetState(BrokerDeviceLifecycleState.Observed, "selection-required");
            }

            return;
        }

        if (selected.IsAmbiguous)
        {
            await DetachCurrentAsync("ambiguous-physical-correlation").ConfigureAwait(false);
            SetState(BrokerDeviceLifecycleState.Observed, "ambiguous-physical-correlation");
            return;
        }

        lock (candidateGate)
        {
            selectedDeviceId = selected.Id;
        }

        if (attachedDeviceId != WheelDeviceId.Automatic && attachedDeviceId != selected.Id)
        {
            await DetachCurrentAsync("selection-changed").ConfigureAwait(false);
        }

        HidDeviceSnapshot selectedDevice = selected.Device;
        WheelIdentity selectedIdentity = selected.Identity;
        if (selectedIdentity.IsPreferredMode)
        {
            HidDeviceSnapshot native = selectedDevice;
            if (IsDeviceReady && string.Equals(attachedDevicePath, native.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await AttachNativeDeviceAsync(
                native, selectedIdentity, selected.Id,
                "already-preferred", false, transition).ConfigureAwait(false);
            return;
        }

        HidDeviceSnapshot source = selectedDevice;
        SetState(BrokerDeviceLifecycleState.Observed, "compatibility-device-observed");
        SetState(BrokerDeviceLifecycleState.Identified, "revision-identified-classic-wheel");
        if (!attemptedConnections.Add(selected.Id))
        {
            return;
        }

        await SwitchAndAttachAsync(
            source, selectedIdentity, selected.Id, transition).ConfigureAwait(false);
    }

    private async Task SwitchAndAttachAsync(
        HidDeviceSnapshot source,
        WheelIdentity identity,
        WheelDeviceId deviceId,
        TransitionLease transition)
    {
        transition.ThrowIfInvalid();
        IReadOnlyList<ModeSwitchStep> steps = identity.Definition.PreferredModeSwitch.Steps;
        if (source.OutputReportByteLength != identity.Presentation.ReportLayout.OutputReportByteLength)
        {
            SetState(BrokerDeviceLifecycleState.Faulted, "unexpected-presentation-output-report-length");
            return;
        }

        SetState(BrokerDeviceLifecycleState.Calibrating, "waiting-for-power-on-calibration");
        try
        {
            _ = await calibrationMonitor.WaitForCompletionAsync(
                source, options.CalibrationTimeout, transition.Token).ConfigureAwait(false);
            transition.ThrowIfInvalid();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(BrokerDeviceLifecycleState.Faulted, $"power-on-calibration-failed:{exception.GetType().Name}");
            return;
        }

        byte[][] reports = steps
            .Select(step => HidOutputReportFormatter.FormatCommand(
                step.Command, step.ReportId, source.OutputReportByteLength))
            .ToArray();
        SetState(BrokerDeviceLifecycleState.Switching, "sending-preferred-mode-sequence");
        transition.ThrowIfInvalid();
        await using (IHidTransport transport = transportFactory.OpenForOutput(source))
        {
            for (int index = 0; index < reports.Length; index++)
            {
                transition.ThrowIfInvalid();
                try
                {
                    await transport.SetOutputReportAsync(reports[index], transition.Token).ConfigureAwait(false);
                }
                catch (Win32Exception exception) when (
                    steps[index].DetachExpected && IsExpectedDetachError(exception.NativeErrorCode))
                {
                }
            }
        }

        SetState(BrokerDeviceLifecycleState.AwaitingNativeMode, $"waiting-for-{identity.Definition.PreferredProductId:X4}");
        long started = Environment.TickCount64;
        while (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) < options.NativeModeTimeout)
        {
            await Task.Delay(options.RescanDebounce, transition.Token).ConfigureAwait(false);
            transition.ThrowIfInvalid();
            IReadOnlyList<HidDeviceSnapshot> devices = await enumerator.EnumerateAsync(transition.Token).ConfigureAwait(false);
            transition.ThrowIfInvalid();
            HidDeviceSnapshot[] candidates = devices
                .Where(device => IsPreferredPresentationOf(device, identity.Definition))
                .Where(device => Correlates(source, device))
                .ToArray();
            if (candidates.Length == 1)
            {
                ObservePresentation(deviceId, candidates[0]);
                await AttachNativeDeviceAsync(
                    candidates[0],
                    new WheelIdentity(identity.Definition, identity.Definition.PreferredPresentation,
                        candidates[0].VendorId, candidates[0].VersionNumber),
                    deviceId,
                    CorrelationMethod(source, candidates[0]), true, transition).ConfigureAwait(false);
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
        WheelIdentity identity,
        WheelDeviceId deviceId,
        string correlation,
        bool settleAfterNativeSwitch,
        TransitionLease transition)
    {
        transition.ThrowIfInvalid();
        Volatile.Write(ref deviceReady, 0);
        SetState(BrokerDeviceLifecycleState.NativeModeReady, correlation);
        var protocol = new ClassicWheelProtocol(identity.Definition);
        if (device.OutputReportByteLength != protocol.ReportLength ||
            device.OutputReportByteLength != identity.Presentation.ReportLayout.OutputReportByteLength)
        {
            SetState(BrokerDeviceLifecycleState.Faulted, "unexpected-native-output-report-length");
            return;
        }

        if (settleAfterNativeSwitch)
        {
            await Task.Delay(options.NativeModeSettleDelay, transition.Token).ConfigureAwait(false);
            transition.ThrowIfInvalid();
        }

        await DetachCurrentAsync("replace-device").ConfigureAwait(false);
        transition.ThrowIfInvalid();
        IHidTransport? transport = null;
        CoalescingHidOutputPump? pump = null;
        bool pumpAttached = false;
        try
        {
            RuntimeSettings remembered;
            lock (candidateGate)
            {
                remembered = settingsByIdentity.GetValueOrDefault(deviceId, RuntimeSettings.Default);
            }

            if (!protocol.IsRangeSupported(remembered.RangeDegrees))
            {
                SetState(BrokerDeviceLifecycleState.Faulted, "remembered-steering-range-unsupported");
                return;
            }

            transition.ThrowIfInvalid();
            runtime.Invoke(
                () => _ = coordinator.SetRuntimeSettings(remembered),
                options.OutputReadyTimeout);
            transition.ThrowIfInvalid();
            transport = transportFactory.OpenForOutput(device);
            await InitializeNativeOutputAsync(transport, protocol, transition).ConfigureAwait(false);
            transition.ThrowIfInvalid();
            pump = new CoalescingHidOutputPump(transport, profileEvents: profileEvents, protocol: protocol);
            transport = null;
            transition.ThrowIfInvalid();
            runtime.Invoke(() =>
            {
                transition.ThrowIfInvalid();
                output.Attach(pump);
                pumpAttached = true;
                runtime.ResetOutputPolicyForAttach(protocol);
            }, options.OutputReadyTimeout);

            RuntimeSettings settings = coordinator.RuntimeSettings;
            var finalAutocenter = new byte[protocol.ReportLength];
            if (settings.IdleAutocenter > 0)
            {
                protocol.WriteEnableAutocenter(finalAutocenter);
            }
            else
            {
                protocol.WriteDisableAutocenter(finalAutocenter);
            }

            using var readyTimeout = new CancellationTokenSource(options.OutputReadyTimeout);
            using var readyLinked = CancellationTokenSource.CreateLinkedTokenSource(
                transition.Token, readyTimeout.Token);
            transition.ThrowIfInvalid();
            await pump.PublishBarrierAndWaitAsync(finalAutocenter, readyLinked.Token).ConfigureAwait(false);
            transition.ThrowIfInvalid();
            attachedDevicePath = device.DevicePath;
            attachedDeviceId = deviceId;
            activeProtocol = protocol;
            lock (candidateGate)
            {
                int index = Array.FindIndex(candidates, candidate => candidate.Id == deviceId);
                if (index >= 0)
                {
                    candidates[index] = new DiscoveredCandidate(deviceId, device, identity, false);
                }
            }

            transition.ThrowIfInvalid();
            Volatile.Write(ref attachedPump, pump);
            Volatile.Write(ref deviceReady, 1);
            SetState(BrokerDeviceLifecycleState.Attached, "managed-broker-output-ready");
        }
        catch (OperationCanceledException) when (transition.IsSuperseded)
        {
            await CleanupPartialAttachAsync(pump, pumpAttached, protocol).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CleanupPartialAttachAsync(pump, pumpAttached, protocol).ConfigureAwait(false);
            SetState(BrokerDeviceLifecycleState.Faulted, $"broker-attach-failed:{exception.GetType().Name}");
        }
        catch (OperationCanceledException)
        {
            await CleanupPartialAttachAsync(pump, pumpAttached, protocol).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transport is not null)
            {
                var stopAll = new byte[protocol.ReportLength];
                protocol.WriteStopAll(stopAll);
                try
                {
                    using var timeout = new CancellationTokenSource(options.OutputReadyTimeout);
                    await transport.WriteOutputReportAsync(stopAll, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                }

                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task InitializeNativeOutputAsync(
        IHidTransport transport,
        ClassicWheelProtocol protocol,
        TransitionLease transition)
    {
        RuntimeSettings settings = coordinator.RuntimeSettings;
        var report = new byte[protocol.ReportLength];
        protocol.WriteStopAll(report);
        transition.ThrowIfInvalid();
        await transport.WriteOutputReportAsync(report, transition.Token).ConfigureAwait(false);
        protocol.WriteDisableAutocenter(report);
        transition.ThrowIfInvalid();
        await transport.WriteOutputReportAsync(report, transition.Token).ConfigureAwait(false);
        foreach (byte[] range in protocol.CreateRangeReports(settings.RangeDegrees))
        {
            transition.ThrowIfInvalid();
            await transport.WriteOutputReportAsync(range, transition.Token).ConfigureAwait(false);
        }

        protocol.WriteAutocenterParameters(report, settings.IdleAutocenter);
        transition.ThrowIfInvalid();
        await transport.WriteOutputReportAsync(report, transition.Token).ConfigureAwait(false);
    }

    private async Task CleanupPartialAttachAsync(
        CoalescingHidOutputPump? pump,
        bool pumpAttached,
        ClassicWheelProtocol protocol)
    {
        Volatile.Write(ref deviceReady, 0);
        CoalescingHidOutputPump? owned = pump;
        if (pumpAttached)
        {
            try
            {
                owned = runtime.Invoke(() => output.Detach(), options.OutputReadyTimeout) ?? pump;
            }
            catch (TimeoutException)
            {
                owned = output.Detach() ?? pump;
            }
        }

        if (owned is null)
        {
            return;
        }

        var stopAll = new byte[protocol.ReportLength];
        protocol.WriteStopAll(stopAll);
        try
        {
            using var timeout = new CancellationTokenSource(options.OutputReadyTimeout);
            await owned.PublishBarrierAndWaitAsync(stopAll, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            owned.Dispose();
        }
    }

    private async Task HandleOutputFaultAsync(CoalescingHidOutputPump failedPump)
    {
        await scanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref attachedPump), failedPump))
            {
                failedPump.Dispose();
                return;
            }

            Volatile.Write(ref attachedPump, null);
            Volatile.Write(ref deviceReady, 0);
            WheelDeviceId failedDeviceId = attachedDeviceId;
            attachedDevicePath = null;
            attachedDeviceId = WheelDeviceId.Automatic;
            try
            {
                runtime.Invoke(() =>
                {
                    if (failedDeviceId != WheelDeviceId.Automatic)
                    {
                        lock (candidateGate)
                        {
                            settingsByIdentity[failedDeviceId] = coordinator.RuntimeSettings;
                        }
                    }

                    coordinator.DeviceRemoved();
                }, options.OutputReadyTimeout);
            }
            catch (TimeoutException)
            {
            }

            failedPump.Dispose();
            SetState(BrokerDeviceLifecycleState.Faulted, "continuous-output-fault");
        }
        finally
        {
            scanGate.Release();
        }
    }

    private async Task DetachCurrentAsync(string reason)
    {
        Volatile.Write(ref deviceReady, 0);
        CoalescingHidOutputPump? logicalPump = Volatile.Read(ref attachedPump);
        if (!output.IsAttached && logicalPump is null)
        {
            attachedDevicePath = null;
            attachedDeviceId = WheelDeviceId.Automatic;
            return;
        }

        try
        {
            runtime.Invoke(() =>
            {
                if (attachedDeviceId != WheelDeviceId.Automatic)
                {
                    lock (candidateGate)
                    {
                        settingsByIdentity[attachedDeviceId] = coordinator.RuntimeSettings;
                    }
                }

                coordinator.DeviceRemoved();
            }, options.OutputReadyTimeout);
        }
        catch (TimeoutException)
        {
        }

        CoalescingHidOutputPump? pump;
        try
        {
            pump = runtime.Invoke(() => output.Detach(), options.OutputReadyTimeout) ?? logicalPump;
        }
        catch (TimeoutException)
        {
            pump = output.Detach() ?? logicalPump;
        }

        Interlocked.CompareExchange(ref attachedPump, null, logicalPump);

        if (pump is not null)
        {
            try
            {
                if (pump.Failure is null)
                {
                    var stopAll = new byte[activeProtocol.ReportLength];
                    activeProtocol.WriteStopAll(stopAll);
                    using var timeout = new CancellationTokenSource(options.OutputReadyTimeout);
                    await pump.PublishBarrierAndWaitAsync(stopAll, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                pump.Dispose();
            }
        }

        attachedDevicePath = null;
        attachedDeviceId = WheelDeviceId.Automatic;
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

    private static bool IsPreferredPresentationOf(HidDeviceSnapshot device, WheelDefinition definition) =>
        device.VendorId == ClassicWheelCatalog.LogitechVendorId &&
        device.ProductId == definition.PreferredProductId &&
        definition.MatchesRevision(device.VersionNumber);

    private static bool Correlates(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId ||
        source.EffectiveLocationPaths.Intersect(candidate.EffectiveLocationPaths, StringComparer.OrdinalIgnoreCase).Any();

    private static string CorrelationMethod(HidDeviceSnapshot source, HidDeviceSnapshot candidate) =>
        source.ContainerId is not null && candidate.ContainerId == source.ContainerId ? "container-id" : "location-path";

    private void LogUnknownCompatibilityPresentations(IEnumerable<HidDeviceSnapshot> devices)
    {
        foreach (HidDeviceSnapshot device in devices.Where(static device =>
                     device.ProductId == ClassicWheelCatalog.CompatibilityProductId &&
                     !ClassicWheelCatalog.TryIdentify(
                         device.VendorId, device.ProductId, device.VersionNumber, out _)))
        {
            BrokerEventSource.Log.ReadOnlyPresentation(
                device.ProductId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
                device.VersionNumber.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
                "unknown-c294-revision");
        }
    }

    private DiscoveredCandidate[] CreatePhysicalCandidates(IEnumerable<HidDeviceSnapshot> devices)
    {
        var presentations = new List<PresentationCandidate>();
        foreach (HidDeviceSnapshot device in devices)
        {
            if (ClassicWheelCatalog.TryIdentify(
                    device.VendorId, device.ProductId, device.VersionNumber, out WheelIdentity? identity) &&
                identity is not null)
            {
                presentations.Add(new PresentationCandidate(device, identity));
            }
        }

        var groups = new List<List<PresentationCandidate>>();
        foreach (PresentationCandidate presentation in presentations)
        {
            int[] matches = groups
                .Select((group, index) => (group, index))
                .Where(value => value.group.Any(existing => SamePhysicalEvidence(existing.Device, presentation.Device)))
                .Select(static value => value.index)
                .ToArray();
            if (matches.Length == 0)
            {
                groups.Add([presentation]);
                continue;
            }

            List<PresentationCandidate> target = groups[matches[0]];
            target.Add(presentation);
            for (int index = matches.Length - 1; index > 0; index--)
            {
                target.AddRange(groups[matches[index]]);
                groups.RemoveAt(matches[index]);
            }
        }

        lock (candidateGate)
        {
            var discovered = new List<DiscoveredCandidate>(groups.Count);
            foreach (List<PresentationCandidate> group in groups)
            {
                PresentationCandidate first = group[0];
                PhysicalWheelRecord[] registryMatches = physicalWheels.Values
                    .Where(record => group.Any(presentation => record.Matches(presentation)))
                    .ToArray();
                bool ambiguous = registryMatches.Length > 1;
                PhysicalWheelRecord record;
                if (registryMatches.Length == 0)
                {
                    var id = new WheelDeviceId(nextDeviceId++);
                    record = new PhysicalWheelRecord(id, first.Identity.Definition.Model, first.Device.VersionNumber);
                    physicalWheels.Add(id, record);
                }
                else
                {
                    record = registryMatches.OrderBy(static value => value.Id.Value).First();
                }

                if (!ambiguous)
                {
                    foreach (PresentationCandidate presentation in group)
                    {
                        record.Absorb(presentation.Device);
                    }
                }

                (PresentationCandidate representative, bool endpointAmbiguous) =
                    SelectRepresentative(group, attachedDevicePath);
                discovered.Add(new DiscoveredCandidate(
                    record.Id,
                    representative.Device,
                    representative.Identity,
                    ambiguous || endpointAmbiguous));
            }

            return discovered
                .OrderBy(static candidate => candidate.Id.Value)
                .Take(ClassicWheelCatalog.MaximumCandidates)
                .ToArray();
        }
    }

    private void ObservePresentation(WheelDeviceId id, HidDeviceSnapshot device)
    {
        lock (candidateGate)
        {
            if (physicalWheels.TryGetValue(id, out PhysicalWheelRecord? record))
            {
                record.Absorb(device);
            }
        }
    }

    private static (PresentationCandidate Representative, bool Ambiguous) SelectRepresentative(
        IReadOnlyList<PresentationCandidate> group,
        string? attachedPath)
    {
        int bestRank = group.Max(PresentationRank);
        PresentationCandidate[] best = group.Where(value => PresentationRank(value) == bestRank).ToArray();
        if (best.Length == 1)
        {
            return (best[0], false);
        }

        PresentationCandidate[] attached = best.Where(value => string.Equals(
            value.Device.DevicePath, attachedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return attached.Length == 1 ? (attached[0], false) : (best[0], true);
    }

    private static int PresentationRank(PresentationCandidate presentation)
    {
        IReadOnlyList<WheelPresentationDefinition> definitions = presentation.Identity.Definition.Presentations;
        for (int index = 0; index < definitions.Count; index++)
        {
            if (definitions[index].ProductId == presentation.Device.ProductId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SamePhysicalEvidence(HidDeviceSnapshot left, HidDeviceSnapshot right)
    {
        if (left.VersionNumber != right.VersionNumber ||
            !ClassicWheelCatalog.TryIdentify(left.VendorId, left.ProductId, left.VersionNumber, out WheelIdentity? leftIdentity) ||
            !ClassicWheelCatalog.TryIdentify(right.VendorId, right.ProductId, right.VersionNumber, out WheelIdentity? rightIdentity) ||
            leftIdentity?.Definition.Model != rightIdentity?.Definition.Model)
        {
            return false;
        }

        if (left.ContainerId is not null && left.ContainerId == right.ContainerId ||
            left.EffectiveLocationPaths.Intersect(right.EffectiveLocationPaths, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        bool comparableStrongEvidence = left.ContainerId is not null && right.ContainerId is not null ||
            left.EffectiveLocationPaths.Count > 0 && right.EffectiveLocationPaths.Count > 0;
        if (comparableStrongEvidence)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(left.ParentInstanceId) &&
                string.Equals(left.ParentInstanceId, right.ParentInstanceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(left.InstanceId, right.InstanceId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedDetachError(int error) => error is 6 or 31 or 995 or 1167;

    private sealed record PresentationCandidate(HidDeviceSnapshot Device, WheelIdentity Identity);

    private sealed record DiscoveredCandidate(
        WheelDeviceId Id,
        HidDeviceSnapshot Device,
        WheelIdentity Identity,
        bool IsAmbiguous);

    private sealed class PhysicalWheelRecord(WheelDeviceId id, WheelModel model, ushort revision)
    {
        private readonly HashSet<Guid> containerIds = [];
        private readonly HashSet<string> locationPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> parentInstanceIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> instanceIds = new(StringComparer.OrdinalIgnoreCase);

        public WheelDeviceId Id { get; } = id;

        public WheelModel Model { get; } = model;

        public ushort Revision { get; } = revision;

        public bool Matches(PresentationCandidate presentation)
        {
            HidDeviceSnapshot device = presentation.Device;
            if (Model != presentation.Identity.Definition.Model || Revision != device.VersionNumber)
            {
                return false;
            }

            if (device.ContainerId is Guid containerId && containerIds.Contains(containerId) ||
                device.EffectiveLocationPaths.Any(locationPaths.Contains))
            {
                return true;
            }

            bool comparableStrongEvidence = device.ContainerId is not null && containerIds.Count > 0 ||
                device.EffectiveLocationPaths.Count > 0 && locationPaths.Count > 0;
            if (comparableStrongEvidence)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(device.ParentInstanceId) && parentInstanceIds.Contains(device.ParentInstanceId) ||
                instanceIds.Contains(device.InstanceId);
        }

        public void Absorb(HidDeviceSnapshot device)
        {
            if (device.ContainerId is Guid containerId)
            {
                containerIds.Add(containerId);
            }

            locationPaths.UnionWith(device.EffectiveLocationPaths);
            if (!string.IsNullOrWhiteSpace(device.ParentInstanceId))
            {
                parentInstanceIds.Add(device.ParentInstanceId);
            }

            instanceIds.Add(device.InstanceId);
        }
    }

    private sealed class TransitionLease(
        BrokerDeviceManager owner,
        long revision,
        CancellationTokenSource source) : IDisposable
    {
        public CancellationTokenSource Source { get; } = source;

        public CancellationToken Token => Source.Token;

        public bool IsSuperseded => Volatile.Read(ref owner.selectionRevision) != revision;

        public void ThrowIfInvalid()
        {
            Token.ThrowIfCancellationRequested();
            if (IsSuperseded)
            {
                throw new OperationCanceledException(Token);
            }
        }

        public void Dispose() => Source.Dispose();
    }
}
