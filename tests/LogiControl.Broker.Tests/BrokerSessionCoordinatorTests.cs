// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class BrokerSessionCoordinatorTests
{
    [Fact]
    public void FirstStartingSessionOwnsWheelUntilItBecomesIdle()
    {
        var coordinator = new BrokerSessionCoordinator(new FakeClock());
        Assert.Equal(BrokerResult.Ok, coordinator.OpenSession(out ulong first));
        Assert.Equal(BrokerResult.Ok, coordinator.OpenSession(out ulong second));
        uint firstHandle = DownloadConstant(coordinator, first);
        uint secondHandle = DownloadConstant(coordinator, second);

        Assert.Equal(BrokerResult.Ok, coordinator.StartEffect(first, firstHandle, 1, false, true));
        Assert.Equal(first, coordinator.OwnerSessionId);
        Assert.Equal(BrokerResult.OtherApplicationHasPriority,
            coordinator.StartEffect(second, secondHandle, 1, false, true));

        Assert.Equal(BrokerResult.Ok, coordinator.StopEffect(first, firstHandle));
        Assert.Equal((ulong)0, coordinator.OwnerSessionId);
        Assert.Equal(BrokerResult.Ok, coordinator.StartEffect(second, secondHandle, 1, false, true));
        Assert.Equal(second, coordinator.OwnerSessionId);
    }

    [Fact]
    public void DelayedEffectClaimsAndRetainsOwnership()
    {
        var coordinator = new BrokerSessionCoordinator(new FakeClock());
        coordinator.OpenSession(out ulong first);
        coordinator.OpenSession(out ulong second);
        var delayed = new ConstantEffectDefinition(
            new EffectCommon(10_000, 200_000, 0, 10_000, 10_000, null), 1_000);
        Assert.Equal(BrokerResult.Ok,
            coordinator.UpsertEffect(first, 0, EffectUpdateMask.All, delayed, false, out uint handle));
        uint secondHandle = DownloadConstant(coordinator, second);

        Assert.Equal(BrokerResult.Ok, coordinator.StartEffect(first, handle, 1, false, true));
        Assert.True(coordinator.HasActiveEffects);
        Assert.Equal(BrokerResult.OtherApplicationHasPriority,
            coordinator.StartEffect(second, secondHandle, 1, false, true));
    }

    [Fact]
    public void OwnerLeaseExpiryStopsForceDestroysStateAndReleasesSlots()
    {
        var clock = new FakeClock();
        var coordinator = new BrokerSessionCoordinator(clock);
        coordinator.OpenSession(out ulong session);
        var condition = new ConditionEffectDefinition(
            Common(EffectCommon.InfiniteDuration), ForceEffectKind.Spring, 0, 5_000, -5_000, 10_000, 10_000, 0);
        coordinator.UpsertEffect(session, 0, EffectUpdateMask.All, condition, false, out uint handle);
        coordinator.StartEffect(session, handle, 1, false, true);
        Assert.Equal(session, coordinator.OwnerSessionId);

        clock.Now = BrokerSessionCoordinator.OwnerLeaseMicroseconds + 1;
        MixerSnapshot snapshot = coordinator.Render();
        Assert.Equal(0, snapshot.SoftwareForce);
        Assert.Equal((ulong)0, coordinator.OwnerSessionId);

        var changes = new List<ConditionChangeKind>();
        while (coordinator.TryDequeueConditionChange(out ConditionSlotChange change))
        {
            changes.Add(change.Change);
        }

        Assert.Equal(new[] { ConditionChangeKind.Start, ConditionChangeKind.Stop }, changes);
        Assert.Equal(BrokerResult.InputLost, coordinator.QueryEffect(session, handle, out _, out _));
    }

    [Fact]
    public void HeartbeatExtendsOwnerLease()
    {
        var clock = new FakeClock();
        var coordinator = new BrokerSessionCoordinator(clock);
        coordinator.OpenSession(out ulong session);
        uint handle = DownloadConstant(coordinator, session);
        coordinator.StartEffect(session, handle, 1, false, true);

        clock.Now = 100_000;
        Assert.Equal(BrokerResult.Ok, coordinator.Heartbeat(session));
        clock.Now = 449_999;
        coordinator.Render();
        Assert.Equal(session, coordinator.OwnerSessionId);
        clock.Now = 450_001;
        coordinator.Render();
        Assert.Equal((ulong)0, coordinator.OwnerSessionId);
    }

    [Fact]
    public void PartialUpdateIsMergedByBrokerAndValidateOnlyDoesNotAllocate()
    {
        var coordinator = new BrokerSessionCoordinator(new FakeClock());
        coordinator.OpenSession(out ulong session);
        var initial = new ConstantEffectDefinition(Common(100_000) with { Gain = 5_000 }, 1_000);
        Assert.Equal(BrokerResult.Ok,
            coordinator.UpsertEffect(session, 0, EffectUpdateMask.All, initial, false, out uint handle));
        var update = new ConstantEffectDefinition(Common(1) with { Gain = 7_000 }, 9_000);
        Assert.Equal(BrokerResult.Ok,
            coordinator.UpsertEffect(session, handle, EffectUpdateMask.Gain, update, false, out uint sameHandle));
        Assert.Equal(handle, sameHandle);
        Assert.Equal(BrokerResult.Ok, coordinator.QueryEffect(session, handle, out EffectDefinition? queried, out _));
        var constant = Assert.IsType<ConstantEffectDefinition>(queried);
        Assert.Equal(1_000, constant.Magnitude);
        Assert.Equal(100_000u, constant.Common.DurationMicroseconds);
        Assert.Equal(7_000, constant.Common.Gain);

        Assert.Equal(BrokerResult.Ok,
            coordinator.UpsertEffect(session, 0, EffectUpdateMask.All, update, true, out uint validateHandle));
        Assert.Equal(0u, validateHandle);
        Assert.Equal(BrokerResult.NotFound, coordinator.QueryEffect(session, validateHandle, out _, out _));
    }

    [Fact]
    public void ChangingBoundAxisIsRejectedButOmittingAxisPreservesIt()
    {
        var coordinator = new BrokerSessionCoordinator(new FakeClock());
        coordinator.OpenSession(out ulong session);
        var initial = new ConstantEffectDefinition(Common(100_000) with { AxisIdentifier = 17 }, 1_000);
        coordinator.UpsertEffect(session, 0, EffectUpdateMask.All, initial, false, out uint handle);
        var differentAxis = initial with { Common = initial.Common with { AxisIdentifier = 18, Gain = 5_000 } };

        Assert.Equal(BrokerResult.InvalidArgument,
            coordinator.UpsertEffect(session, handle, EffectUpdateMask.Axis, differentAxis, false, out _));
        Assert.Equal(BrokerResult.Ok,
            coordinator.UpsertEffect(session, handle, EffectUpdateMask.Gain, differentAxis, false, out _));
        coordinator.QueryEffect(session, handle, out EffectDefinition? queried, out _);
        Assert.Equal(17u, queried!.Common.AxisIdentifier);
        Assert.Equal(5_000, queried.Common.Gain);
    }

    [Fact]
    public void DeviceRemovalInvalidatesAllHandlesAndReleasesOwnershipAndConditionSlots()
    {
        var coordinator = new BrokerSessionCoordinator(new FakeClock());
        coordinator.OpenSession(out ulong owner);
        coordinator.OpenSession(out ulong observer);
        var condition = new ConditionEffectDefinition(
            Common(EffectCommon.InfiniteDuration), ForceEffectKind.Spring, 0, 5_000, -5_000, 10_000, 10_000, 0);
        coordinator.UpsertEffect(owner, 0, EffectUpdateMask.All, condition, false, out uint conditionHandle);
        uint observerHandle = DownloadConstant(coordinator, observer);
        Assert.Equal(BrokerResult.Ok, coordinator.StartEffect(owner, conditionHandle, 1, false, true));

        coordinator.DeviceRemoved();

        Assert.Equal((ulong)0, coordinator.OwnerSessionId);
        Assert.False(coordinator.HasActiveEffects);
        Assert.Equal(BrokerResult.NotFound, coordinator.QueryEffect(owner, conditionHandle, out _, out _));
        Assert.Equal(BrokerResult.NotFound, coordinator.QueryEffect(observer, observerHandle, out _, out _));
        var changes = new List<ConditionChangeKind>();
        while (coordinator.TryDequeueConditionChange(out ConditionSlotChange change))
        {
            changes.Add(change.Change);
        }

        Assert.Equal(new[] { ConditionChangeKind.Start, ConditionChangeKind.Stop }, changes);
    }

    private static uint DownloadConstant(BrokerSessionCoordinator coordinator, ulong session)
    {
        Assert.Equal(BrokerResult.Ok, coordinator.UpsertEffect(session, 0, EffectUpdateMask.All,
            new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 1_000), false, out uint handle));
        return handle;
    }

    private static EffectCommon Common(uint duration) => new(duration, 0, 0, 10_000, 10_000, null);

    private sealed class FakeClock : IMonotonicClock
    {
        public long Now { get; set; }

        public long GetMicroseconds() => Now;
    }
}
