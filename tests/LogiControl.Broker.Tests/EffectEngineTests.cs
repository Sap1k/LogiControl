// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class EffectEngineTests
{
    [Fact]
    public void BrokerAndProtocolIpcBoundariesMatch()
    {
        Assert.Equal(IpcFrameCodec.MaximumFrameLength, BrokerConstants.MaximumMessageBytes);
        Assert.Equal((ushort)1, BrokerConstants.IpcMajorVersion);
    }

    [Fact]
    public void ConditionSlotsAllocateLowestFreeAndRejectFourth()
    {
        var engine = new EffectEngine(new FakeClock());
        uint[] handles = new uint[4];
        for (int i = 0; i < handles.Length; i++)
        {
            Assert.Equal(EngineResult.Ok, engine.Upsert(0, Condition(), false, out handles[i]));
        }

        Assert.Equal(EngineResult.Ok, engine.Start(handles[0]));
        Assert.Equal(EngineResult.Ok, engine.Start(handles[1]));
        Assert.Equal(EngineResult.Ok, engine.Start(handles[2]));
        Assert.Equal(EngineResult.DeviceFull, engine.Start(handles[3]));

        for (int slot = 1; slot <= 3; slot++)
        {
            Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange change));
            Assert.Equal(slot, change.Slot);
            Assert.Equal(ConditionChangeKind.Start, change.Change);
        }
    }

    [Fact]
    public void ConditionSlotSurvivesPauseAndIsReusedAfterStop()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.Upsert(0, Condition(), false, out uint first);
        engine.Upsert(0, Condition(), false, out uint second);
        engine.Start(first);
        engine.TryDequeueConditionChange(out ConditionSlotChange start);

        engine.Pause();
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange paused));
        Assert.Equal(ConditionChangeKind.Stop, paused.Change);
        Assert.Equal(start.Slot, paused.Slot);
        engine.Continue();
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange resume));
        Assert.Equal(ConditionChangeKind.Start, resume.Change);
        Assert.Equal(start.Slot, resume.Slot);

        engine.Stop(first);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange stop));
        Assert.Equal(ConditionChangeKind.Stop, stop.Change);
        engine.Start(second);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange reused));
        Assert.Equal(start.Slot, reused.Slot);
    }

    [Fact]
    public void SineUsesDirectInputQuarterPhases()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        var sine = new PeriodicEffectDefinition(Common(4_000), ForceEffectKind.Sine, 10_000, 0, 0, 4_000);
        engine.Upsert(0, sine, false, out uint handle);
        engine.Start(handle);

        Assert.InRange(engine.Render().SoftwareForce, -3, 3);
        clock.Now = 1_000;
        Assert.InRange(engine.Render().SoftwareForce, 9_995, 10_000);
        clock.Now = 2_000;
        Assert.InRange(engine.Render().SoftwareForce, -3, 3);
        clock.Now = 3_000;
        Assert.InRange(engine.Render().SoftwareForce, -10_000, -9_995);
    }

    [Theory]
    [InlineData(ForceEffectKind.Square, 10_000, 10_000, -10_000, -10_000)]
    [InlineData(ForceEffectKind.Sine, 0, 10_000, 0, -10_000)]
    [InlineData(ForceEffectKind.Triangle, 10_000, 0, -10_000, 0)]
    [InlineData(ForceEffectKind.SawtoothUp, -10_000, -5_000, 0, 5_000)]
    [InlineData(ForceEffectKind.SawtoothDown, 10_000, 5_000, 0, -5_000)]
    public void EveryPeriodicWaveformUsesDirectInputQuarterPhases(
        ForceEffectKind kind,
        int phase0,
        int phase90,
        int phase180,
        int phase270)
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        engine.Upsert(0, new PeriodicEffectDefinition(Common(4_000), kind, 10_000, 0, 0, 4_000), false, out uint handle);
        engine.Start(handle);

        int[] expected = { phase0, phase90, phase180, phase270 };
        for (int quarter = 0; quarter < 4; quarter++)
        {
            clock.Now = quarter * 1_000;
            Assert.InRange(engine.Render().SoftwareForce, expected[quarter] - 3, expected[quarter] + 3);
        }
    }

    [Fact]
    public void PauseFreezesEffectTime()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        engine.Upsert(0, new RampEffectDefinition(Common(10_000), 0, 10_000), false, out uint handle);
        engine.Start(handle);
        clock.Now = 2_000;
        Assert.Equal(2_000, engine.Render().SoftwareForce);

        engine.Pause();
        clock.Now = 7_000;
        Assert.Equal(0, engine.Render().SoftwareForce);
        engine.Continue();
        Assert.Equal(2_000, engine.Render().SoftwareForce);
    }

    [Fact]
    public void StopAllRetainsDownloadsAndResetInvalidatesHandles()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.Upsert(0, new ConstantEffectDefinition(Common(10_000), 1_000), false, out uint handle);
        engine.Start(handle);
        engine.StopAll();
        Assert.True(engine.TryGetState(handle, out EffectPlaybackState state));
        Assert.Equal(EffectPlaybackState.Stopped, state);
        engine.Reset();
        Assert.False(engine.TryGetState(handle, out _));
    }

    [Fact]
    public void CustomEffectUsesSampleAndHold()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        engine.Upsert(0, new CustomEffectDefinition(Common(4_000, 1_000), new[] { 100, 200, 300, 400 }), false, out uint handle);
        engine.Start(handle);
        clock.Now = 1_999;
        Assert.Equal(200, engine.Render().SoftwareForce);
    }

    [Fact]
    public void DelayedConditionReservesSlotButDoesNotActivateFirmwareEarly()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        var delayed = Condition() with
        {
            Common = Common(10_000) with { StartDelayMicroseconds = 5_000 },
        };
        engine.Upsert(0, delayed, false, out uint handle);
        engine.Start(handle);
        Assert.False(engine.TryDequeueConditionChange(out _));
        Assert.Equal((byte)0b10, engine.Render().ConditionSlotMask);
        clock.Now = 4_999;
        engine.Render();
        Assert.False(engine.TryDequeueConditionChange(out _));
        clock.Now = 5_000;
        engine.Render();
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange started));
        Assert.Equal(ConditionChangeKind.Start, started.Change);
        Assert.Equal(1, started.Slot);
    }

    [Fact]
    public void ActuatorMuteStopsConditionsWithoutAdvancingOrReleasingTheirSlots()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.Upsert(0, Condition(), false, out uint handle);
        engine.Start(handle);
        engine.TryDequeueConditionChange(out ConditionSlotChange started);
        engine.SetActuatorsEnabled(false);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange stopped));
        Assert.Equal(ConditionChangeKind.Stop, stopped.Change);
        Assert.Equal(started.Slot, stopped.Slot);
        Assert.True(engine.HasActiveEffects);

        engine.SetActuatorsEnabled(true);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange resumed));
        Assert.Equal(ConditionChangeKind.Start, resumed.Change);
        Assert.Equal(started.Slot, resumed.Slot);
    }

    [Fact]
    public void ConditionGainUpdatesAreAppliedInDocumentedOrder()
    {
        var engine = new EffectEngine(new FakeClock());
        var condition = Condition() with
        {
            Common = Common(EffectCommon.InfiniteDuration) with { Gain = 8_000 },
            PositiveCoefficient = 10_000,
        };
        engine.SetRuntimeSettings(RuntimeSettings.Default with { SpringGain = 5_000, MasterGain = 5_000 });
        engine.SetGameGain(5_000);
        engine.Upsert(0, condition, false, out uint handle);
        engine.Start(handle);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange change));
        Assert.Equal(1_000, change.Definition!.PositiveCoefficient);
    }

    [Fact]
    public void SoftRangeOverridesSlotZeroAtBoundaryAndRestoresCurrentMixOnRelease()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.SetRuntimeSettings(RuntimeSettings.Default with { RangeDegrees = 540, BoundaryForce = 3_000 });
        engine.Upsert(0, new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 1_250), false, out uint handle);
        engine.Start(handle);
        Assert.Equal(1_250, engine.Render().SoftwareForce);

        Assert.Equal(EngineResult.Ok, engine.ObserveSteeringPosition(16_127));
        Assert.Equal(3_000, engine.Render().SoftwareForce);
        engine.Upsert(handle, new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 2_500), false, out _);
        Assert.Equal(3_000, engine.Render().SoftwareForce);
        engine.ObserveSteeringPosition(15_359);
        Assert.Equal(2_500, engine.Render().SoftwareForce);

        engine.ObserveSteeringPosition(256);
        Assert.Equal(-3_000, engine.Render().SoftwareForce);
        engine.ObserveSteeringPosition(1_024);
        Assert.Equal(2_500, engine.Render().SoftwareForce);
    }

    [Fact]
    public void DynamicPeriodicUpdatePreservesPhaseUnlessExplicitlyRestarted()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        var sine = new PeriodicEffectDefinition(Common(10_000), ForceEffectKind.Sine, 10_000, 0, 0, 4_000);
        engine.Upsert(0, sine, false, out uint handle);
        engine.Start(handle);
        clock.Now = 1_000;
        Assert.InRange(engine.Render().SoftwareForce, 9_995, 10_000);

        engine.Upsert(handle, sine with { Magnitude = 5_000 }, false, out _);
        Assert.InRange(engine.Render().SoftwareForce, 4_995, 5_000);
        engine.Start(handle, restart: true);
        Assert.InRange(engine.Render().SoftwareForce, -3, 3);
    }

    [Fact]
    public void EnvelopeRearticulatesForEveryIteration()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        var common = new EffectCommon(1_000, 0, 0, 10_000, 10_000, new EffectEnvelope(0, 500, 10_000, 0));
        engine.Upsert(0, new ConstantEffectDefinition(common, 10_000), false, out uint handle);
        engine.Start(handle, iterations: 2);
        clock.Now = 250;
        Assert.Equal(5_000, engine.Render().SoftwareForce);
        clock.Now = 1_250;
        Assert.Equal(5_000, engine.Render().SoftwareForce);
        clock.Now = 2_000;
        Assert.Equal(0, engine.Render().SoftwareForce);
        Assert.True(engine.TryGetState(handle, out EffectPlaybackState state));
        Assert.Equal(EffectPlaybackState.Completed, state);
    }

    [Fact]
    public void SoloStopsOtherEffectsBeforeConditionSlotAllocation()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.Upsert(0, Condition(), false, out uint first);
        engine.Upsert(0, Condition(), false, out uint solo);
        engine.Start(first);
        engine.TryDequeueConditionChange(out ConditionSlotChange firstStart);
        engine.Start(solo, solo: true);

        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange firstStop));
        Assert.Equal(ConditionChangeKind.Stop, firstStop.Change);
        Assert.Equal(firstStart.Slot, firstStop.Slot);
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange soloStart));
        Assert.Equal(ConditionChangeKind.Start, soloStart.Change);
        Assert.Equal(firstStart.Slot, soloStart.Slot);
        Assert.True(engine.TryGetState(first, out EffectPlaybackState state));
        Assert.Equal(EffectPlaybackState.Stopped, state);
    }

    [Fact]
    public void RampDelayMidpointFiniteEndAndIterationsFollowMonotonicTime()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        var ramp = new RampEffectDefinition(
            Common(4_000) with { StartDelayMicroseconds = 2_000 }, -10_000, 10_000);
        engine.Upsert(0, ramp, false, out uint handle);
        engine.Start(handle, iterations: 2);

        clock.Now = 1_999;
        Assert.Equal(0, engine.Render().SoftwareForce);
        clock.Now = 2_000;
        Assert.Equal(-10_000, engine.Render().SoftwareForce);
        clock.Now = 4_000;
        Assert.Equal(0, engine.Render().SoftwareForce);
        clock.Now = 6_000;
        Assert.Equal(-10_000, engine.Render().SoftwareForce);
        clock.Now = 10_000;
        Assert.Equal(0, engine.Render().SoftwareForce);
        Assert.True(engine.TryGetState(handle, out EffectPlaybackState state));
        Assert.Equal(EffectPlaybackState.Completed, state);
    }

    [Fact]
    public void SoftwareGainOrderIncludesOffsetDirectionClassGameAndMasterThenClampsOnce()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.SetRuntimeSettings(RuntimeSettings.Default with { PeriodicGain = 5_000, MasterGain = 5_000 });
        engine.SetGameGain(5_000);
        var periodic = new PeriodicEffectDefinition(
            Common(EffectCommon.InfiniteDuration) with { Gain = 5_000, Direction = -10_000 },
            ForceEffectKind.Square,
            8_000,
            2_000,
            0,
            4_000);
        engine.Upsert(0, periodic, false, out uint handle);
        engine.Start(handle);

        MixerSnapshot snapshot = engine.Render();
        Assert.Equal(-625, snapshot.SoftwareForce);
        Assert.Equal(625, snapshot.UnclampedPeak);
        Assert.Equal(0, snapshot.ClippedTicks);
    }

    [Fact]
    public void ActuatorsOffSuppressesOutputWhileEffectTimeContinues()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        engine.Upsert(0, new RampEffectDefinition(Common(10_000), 0, 10_000), false, out uint handle);
        engine.Start(handle);
        clock.Now = 2_000;
        engine.SetActuatorsEnabled(false);
        Assert.Equal(0, engine.Render().SoftwareForce);
        clock.Now = 7_000;
        engine.SetActuatorsEnabled(true);
        Assert.Equal(7_000, engine.Render().SoftwareForce);
    }

    [Fact]
    public void SoftwareMixTracksUnclampedPeakAndClipRatioInputs()
    {
        var engine = new EffectEngine(new FakeClock());
        engine.Upsert(0, new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 10_000), false, out uint first);
        engine.Upsert(0, new ConstantEffectDefinition(Common(EffectCommon.InfiniteDuration), 10_000), false, out uint second);
        engine.Start(first);
        engine.Start(second);

        MixerSnapshot snapshot = engine.Render();
        Assert.Equal(10_000, snapshot.SoftwareForce);
        Assert.Equal(20_000, snapshot.UnclampedPeak);
        Assert.Equal(1, snapshot.ClippedTicks);
        Assert.Equal(1, snapshot.RenderedTicks);
    }

    [Fact]
    public void NaturalConditionCompletionReleasesItsSlotForLowestFreeReuse()
    {
        var clock = new FakeClock();
        var engine = new EffectEngine(clock);
        engine.Upsert(0, Condition() with { Common = Common(1_000) }, false, out uint finite);
        engine.Upsert(0, Condition(), false, out uint next);
        engine.Start(finite);
        engine.TryDequeueConditionChange(out ConditionSlotChange started);
        clock.Now = 1_000;
        engine.Render();
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange stopped));
        Assert.Equal(ConditionChangeKind.Stop, stopped.Change);
        Assert.Equal(started.Slot, stopped.Slot);

        Assert.Equal(EngineResult.Ok, engine.Start(next));
        Assert.True(engine.TryDequeueConditionChange(out ConditionSlotChange reused));
        Assert.Equal(started.Slot, reused.Slot);
    }

    private static EffectCommon Common(uint duration, uint samplePeriod = 0) =>
        new(duration, 0, samplePeriod, 10_000, 10_000, null);

    private static ConditionEffectDefinition Condition() =>
        new(Common(EffectCommon.InfiniteDuration), ForceEffectKind.Spring, 0, 5_000, -5_000, 10_000, 10_000, 0);

    private sealed class FakeClock : IMonotonicClock
    {
        public long Now { get; set; }

        public long GetMicroseconds() => Now;
    }
}
