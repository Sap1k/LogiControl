// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class BrokerDeviceManagerTests
{
    private static BrokerDeviceManagerOptions TestOptions { get; } = new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1));

    public static TheoryData<ushort, ushort, ushort, byte[][]> AdditionalWheelSwitchCases => new()
    {
        {
            0x1234, 0xC294, 0xC29B,
            [
                [0, 0xF8, 0x0A, 0, 0, 0, 0, 0],
                [0, 0xF8, 0x09, 0x04, 0x01, 0, 0, 0],
            ]
        },
        { 0x1201, 0xC294, 0xC299, [[0, 0xF8, 0x10, 0, 0, 0, 0, 0]] },
        { 0x1001, 0xC294, 0xC298, [[0, 0xF8, 0x01, 0, 0, 0, 0, 0]] },
    };

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
        Assert.Equal(new byte[] { 0, 0xF3, 0, 0, 0, 0, 0, 0 }, reports[0]);
        Assert.Equal(new byte[] { 0, 0xF5, 0, 0, 0, 0, 0, 0 }, reports[1]);
        Assert.Equal(new byte[] { 0, 0xF8, 0x81, 0x84, 0x03, 0, 0, 0 }, reports[2]);
        Assert.Equal(new byte[] { 0, 0xFE, 0x0D, 0, 0, 0, 0, 0 }, reports[3]);
        Assert.Contains(reports[4..], static report =>
            report.SequenceEqual(new byte[] { 0, 0xF5, 0, 0, 0, 0, 0, 0 }));
        Assert.Empty(transport.SetReports);
    }

    [Fact]
    public async Task CompatibilityModeCorrelationFallsBackToLocationWhenContainerChanges()
    {
        HidDeviceSnapshot compatible = Snapshot(0xC294, "compatibility", Guid.NewGuid());
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", Guid.NewGuid());
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([[compatible], [native]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Attached, fixture.Manager.State);
        Assert.Equal("managed-broker-output-ready", fixture.Manager.LastTransitionReason);
        Assert.Equal(2, factory.Transports.Count);
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
        BrokerWheelCandidate candidate = Assert.Single(fixture.Manager.GetCandidates());
        Assert.Equal(native.DevicePath, candidate.DevicePath);
        Assert.True(candidate.IsSelected);
        Assert.True(candidate.IsReady);
    }

    [Theory]
    [MemberData(nameof(AdditionalWheelSwitchCases))]
    public async Task AdditionalClassicWheelsUseDefinitionDrivenSwitchPlans(
        ushort version,
        ushort sourceProductId,
        ushort preferredProductId,
        byte[][] expectedSwitchReports)
    {
        Guid container = Guid.NewGuid();
        HidDeviceSnapshot source = Snapshot(sourceProductId, "source", container) with { VersionNumber = version };
        HidDeviceSnapshot preferred = Snapshot(preferredProductId, "preferred", container) with { VersionNumber = version };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[source], [preferred]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Manager.IsDeviceReady);
        Assert.Equal(expectedSwitchReports, factory.Transports[0].SetReports);
        Assert.Equal(preferred.DevicePath, factory.Transports[1].Device.DevicePath);
        if (preferredProductId == 0xC298)
        {
            byte[][] initialized = factory.Transports[1].WriteReports.ToArray();
            Assert.Equal(new byte[] { 0, 0xF8, 0x03, 0, 0, 0, 0, 0 }, initialized[2]);
            Assert.Equal(new byte[] { 0, 0x81, 0x0B, 0, 0, 0, 0, 0 }, initialized[3]);
        }
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

    [Fact]
    public async Task TwoCandidatesRemainReadOnlyUntilExplicitSelection()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[dfgt, g27]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Equal("selection-required", fixture.Manager.LastTransitionReason);
        BrokerWheelCandidate[] candidates = fixture.Manager.GetCandidates().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Equal(2, candidates.Select(static candidate => candidate.DeviceId).Distinct().Count());
        Assert.All(candidates, static candidate => Assert.False(candidate.IsSelected));
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task OverlappingDfgtPresentationsAreOnePhysicalCandidateAndPreferNative()
    {
        Guid container = Guid.NewGuid();
        HidDeviceSnapshot compatibility = Snapshot(0xC294, "compatibility", container);
        HidDeviceSnapshot native = Snapshot(0xC29A, "native", container);
        var calibration = new CapturingCalibrationMonitor();
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([[compatibility, native]]), factory, calibration);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        BrokerWheelCandidate candidate = Assert.Single(fixture.Manager.GetCandidates());
        Assert.Equal(native.DevicePath, candidate.DevicePath);
        Assert.True(candidate.IsReady);
        Assert.Equal(0, calibration.WaitCount);
        Assert.Equal(native.DevicePath, Assert.Single(factory.Transports).Device.DevicePath);
    }

    [Fact]
    public async Task TwoIdenticalWheelsOnDifferentPortsRemainSeparate()
    {
        HidDeviceSnapshot first = Snapshot(0xC29A, "first", Guid.NewGuid()) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(2)"],
            ParentInstanceId = "USB\\PORT-2",
        };
        HidDeviceSnapshot second = Snapshot(0xC29A, "second", Guid.NewGuid()) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
            ParentInstanceId = "USB\\PORT-3",
        };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[first, second]]), factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        BrokerWheelCandidate[] candidates = fixture.Manager.GetCandidates().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Equal(2, candidates.Select(static value => value.DeviceId).Distinct().Count());
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task AccumulatedAliasesPreserveIdPinAndSettingsAcrossEvidenceChanges()
    {
        Guid container = Guid.NewGuid();
        HidDeviceSnapshot containerOnly = Snapshot(0xC29A, "container", container) with
        {
            LocationPaths = [],
            ParentInstanceId = "USB\\PHYSICAL-DFGT",
        };
        HidDeviceSnapshot bridge = Snapshot(0xC29A, "bridge", container) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(7)"],
            ParentInstanceId = "USB\\PHYSICAL-DFGT",
        };
        HidDeviceSnapshot locationOnly = Snapshot(0xC29A, "location", Guid.NewGuid()) with
        {
            ContainerId = null,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(7)"],
            ParentInstanceId = "USB\\PHYSICAL-DFGT",
        };
        var enumerator = new MutableEnumerator { Devices = [containerOnly] };
        using var fixture = new DeviceFixture(enumerator, new CapturingTransportFactory());

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        WheelDeviceId id = Assert.Single(fixture.Manager.GetCandidates()).DeviceId;
        Assert.True(await fixture.Manager.SelectAsync(id, TestContext.Current.CancellationToken));
        fixture.SetSettings(RuntimeSettings.Default with { RangeDegrees = 540 });

        enumerator.Devices = [bridge];
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        enumerator.Devices = [locationOnly];
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        BrokerWheelCandidate candidate = Assert.Single(fixture.Manager.GetCandidates());
        Assert.Equal(id, candidate.DeviceId);
        Assert.True(candidate.IsSelected);
        Assert.Equal(540, fixture.GetSettings().RangeDegrees);
    }

    [Fact]
    public async Task EvidenceMatchingTwoRegistryRecordsRemainsReadOnly()
    {
        Guid firstContainer = Guid.NewGuid();
        Guid secondContainer = Guid.NewGuid();
        HidDeviceSnapshot first = Snapshot(0xC29A, "first", firstContainer) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(2)"],
            ParentInstanceId = "USB\\FIRST",
        };
        HidDeviceSnapshot second = Snapshot(0xC29A, "second", secondContainer) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
            ParentInstanceId = "USB\\SECOND",
        };
        HidDeviceSnapshot ambiguous = Snapshot(0xC29A, "ambiguous", firstContainer) with
        {
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
            ParentInstanceId = "USB\\UNKNOWN",
        };
        var enumerator = new MutableEnumerator { Devices = [first, second] };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(enumerator, factory);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        enumerator.Devices = [ambiguous];
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Equal("ambiguous-physical-correlation", fixture.Manager.LastTransitionReason);
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task RemovalClearsModeSwitchSuppressionSoReconnectCanRetry()
    {
        HidDeviceSnapshot compatibility = Snapshot(0xC294, "compatibility", Guid.NewGuid());
        var enumerator = new MutableEnumerator { Devices = [compatibility] };
        var calibration = new CapturingCalibrationMonitor { Failure = new TimeoutException("injected") };
        using var fixture = new DeviceFixture(enumerator, new CapturingTransportFactory(), calibration);

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, calibration.WaitCount);

        enumerator.Devices = [];
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        enumerator.Devices = [compatibility];
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, calibration.WaitCount);
    }

    [Fact]
    public async Task DfpRejectsUnsupportedRuntimeRangeWithoutWritingHid()
    {
        HidDeviceSnapshot dfp = Snapshot(0xC298, "dfp", Guid.NewGuid()) with { VersionNumber = 0x1001 };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[dfp]]), factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        CapturingTransport transport = Assert.Single(factory.Transports);
        int before = transport.WriteReports.Count;

        BrokerResult result = fixture.SetSettings(RuntimeSettings.Default with { RangeDegrees = 540 });

        Assert.Equal(BrokerResult.InvalidArgument, result);
        Assert.Equal(before, transport.WriteReports.Count);
        Assert.Equal(900, fixture.GetSettings().RangeDegrees);
    }

    [Fact]
    public async Task SelectionSupersedesBlockedCalibrationWithoutOpeningOldWheel()
    {
        HidDeviceSnapshot dfgtNative = Snapshot(0xC29A, "dfgt-native", Guid.NewGuid());
        HidDeviceSnapshot dfgtCompatibility = Snapshot(0xC294, "dfgt-compatibility", dfgtNative.ContainerId!.Value);
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var enumerator = new MutableEnumerator { Devices = [dfgtNative, g27] };
        var calibration = new BlockingCalibrationMonitor();
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(enumerator, factory, calibration);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] initial = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = initial.Single(static value => value.Model == WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = initial.Single(static value => value.Model == WheelModel.G27).DeviceId;
        enumerator.Devices = [dfgtCompatibility, g27];

        Task oldSelection = fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken);
        await calibration.Started.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Task<bool> newSelection = fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken);

        await oldSelection;
        Assert.True(await newSelection);
        CapturingTransport transport = Assert.Single(factory.Transports);
        Assert.Equal(g27.DevicePath, transport.Device.DevicePath);
        Assert.True(fixture.Manager.IsDeviceReady);
        Assert.NotEqual(BrokerDeviceLifecycleState.Faulted, fixture.Manager.State);
    }

    [Fact]
    public async Task SelectionSupersedesNativePollingWithoutWritingOldWheelAgain()
    {
        HidDeviceSnapshot dfgtNative = Snapshot(0xC29A, "dfgt-native", Guid.NewGuid());
        HidDeviceSnapshot dfgtCompatibility = Snapshot(0xC294, "dfgt-compatibility", dfgtNative.ContainerId!.Value);
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var enumerator = new BlockingCallEnumerator(
            blockCall: 3,
            beforeBlock: [[dfgtNative, g27], [dfgtCompatibility, g27]],
            afterBlock: [dfgtCompatibility, g27]);
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(enumerator, factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] initial = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = initial.Single(static value => value.Model == WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = initial.Single(static value => value.Model == WheelModel.G27).DeviceId;

        Task oldSelection = fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken);
        await enumerator.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        CapturingTransport oldTransport = Assert.Single(factory.Transports);
        Assert.Equal(2, oldTransport.SetReports.Count);
        Task<bool> newSelection = fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken);

        await oldSelection;
        Assert.True(await newSelection);
        Assert.Empty(oldTransport.WriteReports);
        Assert.Equal(1, oldTransport.DisposeCount);
        Assert.Equal(g27.DevicePath, factory.Transports[^1].Device.DevicePath);
    }

    [Fact]
    public async Task SelectionSupersedesNativeSettleBeforeOpeningOldNativeEndpoint()
    {
        HidDeviceSnapshot dfgtNative = Snapshot(0xC29A, "dfgt-native", Guid.NewGuid());
        HidDeviceSnapshot dfgtCompatibility = Snapshot(0xC294, "dfgt-compatibility", dfgtNative.ContainerId!.Value);
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([
                [dfgtNative, g27],
                [dfgtCompatibility, g27],
                [dfgtNative, g27],
                [dfgtNative, g27],
            ]),
            factory,
            options: TestOptions with { NativeModeSettleDelay = TimeSpan.FromSeconds(30) });
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] initial = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = initial.Single(static value => value.Model == WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = initial.Single(static value => value.Model == WheelModel.G27).DeviceId;

        Task oldSelection = fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Manager.State == BrokerDeviceLifecycleState.NativeModeReady,
            TimeSpan.FromSeconds(1)));
        Task<bool> newSelection = fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken);

        await oldSelection;
        Assert.True(await newSelection);
        Assert.DoesNotContain(factory.Transports, value => value.Device.DevicePath == dfgtNative.DevicePath);
        Assert.Equal(g27.DevicePath, factory.Transports[^1].Device.DevicePath);
    }

    [Fact]
    public async Task SelectionDuringInitializationStopsAndDisposesPartialOldTransport()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var enumerator = new MutableEnumerator { Devices = [dfgt, g27] };
        var factory = new GatedTransportFactory(dfgt.DevicePath, blockWrite: 2, honorCancellation: true);
        using var fixture = new DeviceFixture(enumerator, factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] initial = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = initial.Single(static value => value.Model == WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = initial.Single(static value => value.Model == WheelModel.G27).DeviceId;

        Task oldSelection = fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken);
        await factory.Gated!.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Task<bool> newSelection = fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken);

        await oldSelection;
        Assert.True(await newSelection);
        Assert.Equal(1, factory.Gated.DisposeCount);
        Assert.All(factory.Gated.WriteReports, static report => Assert.Equal((byte)0xF3, report[1]));
        Assert.Equal(g27.DevicePath, factory.Capturing[^1].Device.DevicePath);
    }

    [Fact]
    public async Task SelectionAtFinalReadyBarrierStopsAndDisposesPartiallyAttachedPump()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var enumerator = new MutableEnumerator { Devices = [dfgt, g27] };
        var factory = new GatedTransportFactory(dfgt.DevicePath, blockWrite: 5, honorCancellation: false);
        using var fixture = new DeviceFixture(enumerator, factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] initial = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = initial.Single(static value => value.Model == WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = initial.Single(static value => value.Model == WheelModel.G27).DeviceId;

        Task oldSelection = fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken);
        await factory.Gated!.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Task<bool> newSelection = fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken);
        factory.Gated.Release.TrySetResult();

        await oldSelection;
        Assert.True(await newSelection);
        Assert.Equal(1, factory.Gated.DisposeCount);
        Assert.Equal((byte)0xF3, factory.Gated.WriteReports.Last()[1]);
        Assert.Equal(g27.DevicePath, factory.Capturing[^1].Device.DevicePath);
        Assert.True(fixture.Manager.IsDeviceReady);
    }

    [Fact]
    public async Task ExplicitSelectionAttachesOnlyTheChosenCandidate()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([[dfgt, g27], [dfgt, g27]]), factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        WheelDeviceId g27Id = fixture.Manager.GetCandidates().Single(
            static candidate => candidate.Model == Protocol.WheelModel.G27).DeviceId;

        Assert.True(await fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken));

        CapturingTransport transport = Assert.Single(factory.Transports);
        Assert.Equal(g27.DevicePath, transport.Device.DevicePath);
        Assert.True(fixture.Manager.IsDeviceReady);
        BrokerWheelCandidate selected = Assert.Single(
            fixture.Manager.GetCandidates(), static candidate => candidate.IsSelected);
        Assert.Equal(g27Id, selected.DeviceId);
        Assert.True(selected.IsReady);
    }

    [Fact]
    public async Task SelectionChangeStopsAndClosesOldWheelBeforeAttachingNewWheel()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        IReadOnlyList<HidDeviceSnapshot> both = [dfgt, g27];
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(new SequenceEnumerator([both, both, both]), factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] candidates = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = candidates.Single(static candidate => candidate.Model == Protocol.WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = candidates.Single(static candidate => candidate.Model == Protocol.WheelModel.G27).DeviceId;
        Assert.True(await fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken));
        CapturingTransport oldTransport = Assert.Single(factory.Transports);

        Assert.True(await fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken));

        Assert.Equal(2, factory.Transports.Count);
        Assert.Equal(1, oldTransport.DisposeCount);
        Assert.Equal((byte)0xF3, oldTransport.WriteReports.Last()[1]);
        Assert.Equal(g27.DevicePath, factory.Transports[1].Device.DevicePath);
    }

    [Fact]
    public async Task DelayedFaultFromReplacedPumpDoesNotInvalidateReplacementWheel()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var enumerator = new MutableEnumerator { Devices = [dfgt, g27] };
        var factory = new FaultInjectingTransportFactory(dfgt.DevicePath);
        var notifications = new CompletableNotifications();
        using var fixture = new DeviceFixture(enumerator, factory, notifications: notifications);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] candidates = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = candidates.Single(
            static candidate => candidate.Model == Protocol.WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = candidates.Single(
            static candidate => candidate.Model == Protocol.WheelModel.G27).DeviceId;
        Assert.True(await fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken));

        factory.Faulting!.FailNextWrite();
        Assert.Equal(BrokerResult.Ok, fixture.SetSettings(RuntimeSettings.Default with { RangeDegrees = 540 }));
        await factory.Faulting.Failed.Task.WaitAsync(
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(
            () => !fixture.Manager.IsDeviceReady,
            TimeSpan.FromSeconds(1)));
        Assert.True(await fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken));
        int callsBeforeRun = enumerator.CallCount;

        Task run = fixture.Manager.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => enumerator.CallCount >= callsBeforeRun + 2,
                TimeSpan.FromSeconds(1)));
            Assert.True(fixture.Manager.IsDeviceReady);
            Assert.Equal(BrokerDeviceLifecycleState.Attached, fixture.Manager.State);
            CapturingTransport replacement = Assert.Single(factory.Capturing);
            Assert.Equal(g27.DevicePath, replacement.Device.DevicePath);
            Assert.Equal(0, replacement.DisposeCount);
        }
        finally
        {
            notifications.Complete();
            await run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PinnedRemovalNeverTransfersOutputToRemainingWheel()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        var factory = new CapturingTransportFactory();
        using var fixture = new DeviceFixture(
            new SequenceEnumerator([[dfgt, g27], [dfgt, g27], [g27]]), factory);
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        WheelDeviceId dfgtId = fixture.Manager.GetCandidates().Single(
            static candidate => candidate.Model == Protocol.WheelModel.DrivingForceGT).DeviceId;
        Assert.True(await fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken));

        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Manager.IsDeviceReady);
        Assert.Equal(BrokerDeviceLifecycleState.Absent, fixture.Manager.State);
        Assert.Equal("selected-device-absent", fixture.Manager.LastTransitionReason);
        Assert.Single(factory.Transports);
        Assert.False(Assert.Single(fixture.Manager.GetCandidates()).IsSelected);
    }

    [Fact]
    public async Task RuntimeSettingsAreRememberedPerWheelIdentityDuringBrokerLifetime()
    {
        HidDeviceSnapshot dfgt = Snapshot(0xC29A, "dfgt", Guid.NewGuid());
        HidDeviceSnapshot g27 = Snapshot(0xC29B, "g27", Guid.NewGuid()) with
        {
            VersionNumber = 0x1234,
            LocationPaths = ["PCIROOT(0)#USBROOT(0)#USB(3)"],
        };
        IReadOnlyList<HidDeviceSnapshot> both = [dfgt, g27];
        using var fixture = new DeviceFixture(new SequenceEnumerator([both, both, both, both]),
            new CapturingTransportFactory());
        await fixture.Manager.ScanOnceAsync(TestContext.Current.CancellationToken);
        BrokerWheelCandidate[] candidates = fixture.Manager.GetCandidates().ToArray();
        WheelDeviceId dfgtId = candidates.Single(static candidate => candidate.Model == Protocol.WheelModel.DrivingForceGT).DeviceId;
        WheelDeviceId g27Id = candidates.Single(static candidate => candidate.Model == Protocol.WheelModel.G27).DeviceId;
        Assert.True(await fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken));
        fixture.SetSettings(RuntimeSettings.Default with { RangeDegrees = 540 });

        Assert.True(await fixture.Manager.SelectAsync(g27Id, TestContext.Current.CancellationToken));
        Assert.Equal(900, fixture.GetSettings().RangeDegrees);
        fixture.SetSettings(RuntimeSettings.Default with { RangeDegrees = 720 });
        Assert.True(await fixture.Manager.SelectAsync(dfgtId, TestContext.Current.CancellationToken));

        Assert.Equal(540, fixture.GetSettings().RangeDegrees);
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
        private readonly BrokerSessionCoordinator coordinator;

        public DeviceFixture(
            IHidDeviceEnumerator enumerator,
            IHidTransportFactory factory,
            IHidCalibrationMonitor? calibration = null,
            BrokerDeviceManagerOptions? options = null,
            IHidNotificationSource? notifications = null)
        {
            var clock = new QpcMonotonicClock();
            coordinator = new BrokerSessionCoordinator(clock);
            runtime = new EffectRuntime(coordinator, clock, output);
            runtime.Start();
            Manager = new BrokerDeviceManager(
                enumerator,
                notifications ?? new SilentNotifications(),
                factory,
                calibration ?? new CapturingCalibrationMonitor(),
                coordinator,
                runtime,
                output,
                options ?? TestOptions);
        }

        public BrokerDeviceManager Manager { get; }

        public RuntimeSettings GetSettings() => runtime.Invoke(
            () => coordinator.RuntimeSettings, TimeSpan.FromSeconds(1));

        public BrokerResult SetSettings(RuntimeSettings settings) => runtime.Invoke(
            () => Manager.SetRuntimeSettings(settings), TimeSpan.FromSeconds(1));

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

    private sealed class MutableEnumerator : IHidDeviceEnumerator
    {
        private int calls;

        public IReadOnlyList<HidDeviceSnapshot> Devices { get; set; } = [];

        public int CallCount => Volatile.Read(ref calls);

        public ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(Devices);
        }
    }

    private sealed class BlockingCallEnumerator(
        int blockCall,
        IReadOnlyList<IReadOnlyList<HidDeviceSnapshot>> beforeBlock,
        IReadOnlyList<HidDeviceSnapshot> afterBlock) : IHidDeviceEnumerator
    {
        private int calls;

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == blockCall)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return call < blockCall
                ? beforeBlock[Math.Min(call - 1, beforeBlock.Count - 1)]
                : afterBlock;
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

    private sealed class CompletableNotifications : IHidNotificationSource
    {
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => completion.TrySetResult(false);

        public IAsyncEnumerable<HidDeviceChange> WatchAsync(CancellationToken cancellationToken = default) =>
            new NotificationEnumerable(completion.Task, cancellationToken);

        private sealed class NotificationEnumerable(
            Task<bool> completion,
            CancellationToken cancellationToken) :
            IAsyncEnumerable<HidDeviceChange>, IAsyncEnumerator<HidDeviceChange>
        {
            public HidDeviceChange Current => default;

            public IAsyncEnumerator<HidDeviceChange> GetAsyncEnumerator(
                CancellationToken ignoredCancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() =>
                new(completion.WaitAsync(cancellationToken));

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class BlockingCalibrationMonitor : IHidCalibrationMonitor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
            HidDeviceSnapshot device,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SteeringCalibrationObservation(4, 0, 16_383, 8_192);
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

    private sealed class GatedTransportFactory(
        string gatedPath,
        int blockWrite,
        bool honorCancellation) : IHidTransportFactory
    {
        public GatedTransport? Gated { get; private set; }

        public List<CapturingTransport> Capturing { get; } = [];

        public IHidTransport OpenForOutput(HidDeviceSnapshot device)
        {
            if (string.Equals(device.DevicePath, gatedPath, StringComparison.OrdinalIgnoreCase))
            {
                Gated = new GatedTransport(device, blockWrite, honorCancellation);
                return Gated;
            }

            var transport = new CapturingTransport(device);
            Capturing.Add(transport);
            return transport;
        }
    }

    private sealed class FaultInjectingTransportFactory(string faultingPath) : IHidTransportFactory
    {
        public FaultInjectingTransport? Faulting { get; private set; }

        public List<CapturingTransport> Capturing { get; } = [];

        public IHidTransport OpenForOutput(HidDeviceSnapshot device)
        {
            if (string.Equals(device.DevicePath, faultingPath, StringComparison.OrdinalIgnoreCase))
            {
                Faulting = new FaultInjectingTransport(device);
                return Faulting;
            }

            var transport = new CapturingTransport(device);
            Capturing.Add(transport);
            return transport;
        }
    }

    private sealed class FaultInjectingTransport(HidDeviceSnapshot device) : IHidTransport
    {
        private int failNextWrite;
        private int disposeCount;

        public HidDeviceSnapshot Device { get; } = device;

        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void FailNextWrite() => Volatile.Write(ref failNextWrite, 1);

        public ValueTask SetOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref failNextWrite, 0) != 0)
            {
                Failed.TrySetResult();
                return ValueTask.FromException(new IOException("Injected output failure."));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedTransport(
        HidDeviceSnapshot device,
        int blockWrite,
        bool honorCancellation) : IHidTransport
    {
        private int writes;
        private int disposeCount;

        public HidDeviceSnapshot Device { get; } = device;

        public ConcurrentQueue<byte[]> WriteReports { get; } = new();

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public ValueTask SetOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask WriteOutputReportAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken = default)
        {
            int write = Interlocked.Increment(ref writes);
            if (write == blockWrite)
            {
                Blocked.TrySetResult();
                if (honorCancellation)
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await Release.Task;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteReports.Enqueue(report.ToArray());
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
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
