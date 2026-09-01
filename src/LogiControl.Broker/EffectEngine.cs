// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker;

public enum EngineResult
{
    Ok,
    InvalidArgument,
    NotFound,
    DeviceFull,
    StaleHandle,
}

public enum ConditionChangeKind : byte
{
    Start,
    Update,
    Stop,
}

public readonly record struct ConditionSlotChange(
    ConditionChangeKind Change,
    int Slot,
    uint Handle,
    ConditionEffectDefinition? Definition);

public readonly record struct MixerSnapshot(
    long TimestampMicroseconds,
    int SoftwareForce,
    int ActiveEffectCount,
    byte ConditionSlotMask,
    long UnclampedPeak,
    long ClippedTicks,
    long RenderedTicks);

public sealed class EffectEngine : IRuntimeMixer
{
    private const int EffectCapacity = EffectDefinitionValidator.MaximumEffectsPerSession;
    private const int ConditionQueueCapacity = 64;
    private const int SineTableBits = 12;
    private const int SineTableLength = 1 << SineTableBits;
    private const int RangeLockEngageLow = 256;
    private const int RangeLockReleaseLow = 1024;
    private const int RangeLockEngageHigh = 16_127;
    private const int RangeLockReleaseHigh = 15_359;
    private static readonly short[] SineTable = BuildSineTable();

    private readonly IMonotonicClock clock;
    private readonly Entry?[] entries = new Entry[EffectCapacity];
    private readonly int[] conditionOwners = new int[3] { -1, -1, -1 };
    private readonly ConditionSlotChange[] conditionQueue = new ConditionSlotChange[ConditionQueueCapacity];
    private int conditionRead;
    private int conditionWrite;
    private uint nextHandle = 1;
    private RuntimeSettings settings = RuntimeSettings.Default;
    private int gameGain = 10_000;
    private bool paused;
    private bool actuatorsEnabled = true;
    private long pausedAt;
    private long unclampedPeak;
    private long clippedTicks;
    private long renderedTicks;
    private int rangeLockState;
    private bool stopAllBarrierPending;

    public EffectEngine(IMonotonicClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public RuntimeSettings Settings => settings;

    RuntimeSettings IRuntimeMixer.RuntimeSettings => settings;

    public bool IsPaused => paused;

    public bool ActuatorsEnabled => actuatorsEnabled;

    public int DownloadedEffectCount
    {
        get
        {
            int count = 0;
            foreach (Entry? entry in entries)
            {
                if (entry is not null) count++;
            }

            return count;
        }
    }

    public EngineResult Validate(EffectDefinition definition) =>
        EffectDefinitionValidator.TryValidate(definition, out _) ? EngineResult.Ok : EngineResult.InvalidArgument;

    public EngineResult Upsert(uint handle, EffectDefinition definition, bool validateOnly, out uint assignedHandle)
    {
        assignedHandle = 0;
        if (!EffectDefinitionValidator.TryValidate(definition, out _))
        {
            return EngineResult.InvalidArgument;
        }

        if (validateOnly)
        {
            assignedHandle = handle;
            return EngineResult.Ok;
        }

        if (handle == 0)
        {
            int free = FindFreeEntry();
            if (free < 0)
            {
                return EngineResult.DeviceFull;
            }

            uint newHandle = AllocateHandle();
            entries[free] = new Entry(newHandle, definition);
            assignedHandle = newHandle;
            return EngineResult.Ok;
        }

        int index = FindEntry(handle);
        if (index < 0)
        {
            return EngineResult.StaleHandle;
        }

        Entry entry = entries[index]!;
        bool kindChanged = entry.Definition.Kind != definition.Kind;
        if (kindChanged && entry.ConditionSlot >= 0)
        {
            ReleaseCondition(index, entry);
        }

        entry.Definition = definition;
        if (entry.ConditionSlot >= 0 && entry.ConditionStarted)
        {
            EnqueueCondition(new ConditionSlotChange(
                ConditionChangeKind.Update,
                entry.ConditionSlot,
                entry.Handle,
                PrepareConditionDefinition((ConditionEffectDefinition)definition)));
        }

        assignedHandle = handle;
        return EngineResult.Ok;
    }

    public EngineResult Start(uint handle, uint iterations = 1, bool solo = false, bool restart = true)
    {
        int index = FindEntry(handle);
        if (index < 0)
        {
            return EngineResult.StaleHandle;
        }

        Entry entry = entries[index]!;
        if (!restart && entry.State is EffectPlaybackState.Delayed or EffectPlaybackState.Playing or EffectPlaybackState.Paused)
        {
            return EngineResult.Ok;
        }

        if (solo)
        {
            StopAll();
        }

        if (IsCondition(entry.Definition.Kind) && entry.ConditionSlot < 0)
        {
            int ownerIndex = FindFreeConditionOwner();
            if (ownerIndex < 0)
            {
                return EngineResult.DeviceFull;
            }

            conditionOwners[ownerIndex] = index;
            entry.ConditionSlot = ownerIndex + 1;
        }

        long now = clock.GetMicroseconds();
        entry.StartMicroseconds = checked(now + entry.Definition.Common.StartDelayMicroseconds);
        entry.Iterations = iterations == 0 ? 1 : iterations;
        entry.State = entry.Definition.Common.StartDelayMicroseconds == 0
            ? EffectPlaybackState.Playing
            : EffectPlaybackState.Delayed;
        if (entry.State == EffectPlaybackState.Playing && actuatorsEnabled && !paused)
        {
            ActivateCondition(entry);
        }

        return EngineResult.Ok;
    }

    public EngineResult Stop(uint handle)
    {
        int index = FindEntry(handle);
        if (index < 0)
        {
            return EngineResult.StaleHandle;
        }

        StopEntry(index, entries[index]!);
        return EngineResult.Ok;
    }

    public EngineResult Destroy(uint handle)
    {
        int index = FindEntry(handle);
        if (index < 0)
        {
            return EngineResult.StaleHandle;
        }

        Entry entry = entries[index]!;
        StopEntry(index, entry);
        entries[index] = null;
        return EngineResult.Ok;
    }

    public void StopAll()
    {
        stopAllBarrierPending = true;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is { } entry)
            {
                StopEntry(i, entry);
            }
        }
    }

    public bool TryConsumeStopAllBarrier()
    {
        bool pending = stopAllBarrierPending;
        stopAllBarrierPending = false;
        return pending;
    }

    public void Reset()
    {
        StopAll();
        Array.Clear(entries);
    }

    public void Pause()
    {
        if (paused)
        {
            return;
        }

        paused = true;
        pausedAt = clock.GetMicroseconds();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is { State: EffectPlaybackState.Playing or EffectPlaybackState.Delayed } entry)
            {
                DeactivateCondition(entry);
                entry.StateBeforePause = entry.State;
                entry.State = EffectPlaybackState.Paused;
            }
        }
    }

    public void Continue()
    {
        if (!paused)
        {
            return;
        }

        long now = clock.GetMicroseconds();
        long pauseDuration = now - pausedAt;
        paused = false;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is { State: EffectPlaybackState.Paused } entry)
            {
                entry.StartMicroseconds += pauseDuration;
                entry.State = entry.StateBeforePause;
                if (entry.State == EffectPlaybackState.Playing && actuatorsEnabled)
                {
                    ActivateCondition(entry);
                }
            }
        }
    }

    public void SetActuatorsEnabled(bool enabled)
    {
        if (actuatorsEnabled == enabled)
        {
            return;
        }

        actuatorsEnabled = enabled;
        foreach (Entry? entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (!enabled)
            {
                DeactivateCondition(entry);
            }
            else if (!paused && entry.State == EffectPlaybackState.Playing)
            {
                ActivateCondition(entry);
            }
        }
    }

    public EngineResult SetGameGain(int gain)
    {
        if (gain is < 0 or > 10_000)
        {
            return EngineResult.InvalidArgument;
        }

        gameGain = gain;
        RefreshActiveConditions();
        return EngineResult.Ok;
    }

    public EngineResult SetRuntimeSettings(RuntimeSettings value)
    {
        if (!EffectDefinitionValidator.TryValidate(value, out _))
        {
            return EngineResult.InvalidArgument;
        }

        settings = value;
        if (settings.RangeDegrees >= 900)
        {
            rangeLockState = 0;
        }

        RefreshActiveConditions();
        return EngineResult.Ok;
    }

    public EngineResult ObserveSteeringPosition(int rawPosition)
    {
        if (rawPosition is < 0 or > 16_383)
        {
            return EngineResult.InvalidArgument;
        }

        if (settings.RangeDegrees >= 900)
        {
            rangeLockState = 0;
            return EngineResult.Ok;
        }

        if (rangeLockState == 0)
        {
            if (rawPosition <= RangeLockEngageLow)
            {
                rangeLockState = -1;
            }
            else if (rawPosition >= RangeLockEngageHigh)
            {
                rangeLockState = 1;
            }
        }
        else if (rangeLockState < 0 && rawPosition >= RangeLockReleaseLow)
        {
            rangeLockState = 0;
        }
        else if (rangeLockState > 0 && rawPosition <= RangeLockReleaseHigh)
        {
            rangeLockState = 0;
        }

        return EngineResult.Ok;
    }

    public bool TryGetState(uint handle, out EffectPlaybackState state)
    {
        int index = FindEntry(handle);
        state = index < 0 ? default : entries[index]!.State;
        return index >= 0;
    }

    public bool TryGetDefinition(uint handle, out EffectDefinition? definition)
    {
        int index = FindEntry(handle);
        definition = index < 0 ? null : entries[index]!.Definition;
        return index >= 0;
    }

    public bool HasActiveEffects
    {
        get
        {
            foreach (Entry? entry in entries)
            {
                if (entry?.State is EffectPlaybackState.Delayed or EffectPlaybackState.Playing or EffectPlaybackState.Paused)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public MixerSnapshot Render()
    {
        long now = paused ? pausedAt : clock.GetMicroseconds();
        long mixed = 0;
        int active = 0;
        byte conditionMask = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is not { } entry ||
                entry.State is not (EffectPlaybackState.Delayed or EffectPlaybackState.Playing or EffectPlaybackState.Paused))
            {
                continue;
            }

            active++;
            if (entry.ConditionSlot >= 0)
            {
                conditionMask |= (byte)(1 << entry.ConditionSlot);
            }

            if (entry.State == EffectPlaybackState.Paused || now < entry.StartMicroseconds)
            {
                continue;
            }

            entry.State = EffectPlaybackState.Playing;
            if (entry.ConditionSlot >= 0 && actuatorsEnabled)
            {
                ActivateCondition(entry);
            }

            long elapsed = now - entry.StartMicroseconds;
            uint duration = entry.Definition.Common.DurationMicroseconds;
            if (duration != EffectCommon.InfiniteDuration)
            {
                ulong total = (ulong)duration * entry.Iterations;
                if ((ulong)elapsed >= total)
                {
                    int completedConditionSlot = entry.ConditionSlot;
                    CompleteEntry(i, entry);
                    active--;
                    if (completedConditionSlot >= 0)
                    {
                        conditionMask &= (byte)~(1 << completedConditionSlot);
                    }

                    continue;
                }
            }

            if (entry.ConditionSlot >= 0)
            {
                continue;
            }

            mixed += EvaluateSoftware(entry.Definition, elapsed);
        }

        long scaled = Scale(mixed, gameGain);
        scaled = Scale(scaled, settings.MasterGain);
        unclampedPeak = Math.Max(unclampedPeak, Math.Abs(scaled));
        if (scaled is < -10_000 or > 10_000)
        {
            clippedTicks++;
        }

        renderedTicks++;
        int output = actuatorsEnabled && !paused
            ? rangeLockState == 0
                ? (int)Math.Clamp(scaled, -10_000, 10_000)
                : rangeLockState * settings.BoundaryForce
            : 0;
        return new MixerSnapshot(now, output, active, conditionMask, unclampedPeak, clippedTicks, renderedTicks);
    }

    public bool TryDequeueConditionChange(out ConditionSlotChange change)
    {
        if (conditionRead == conditionWrite)
        {
            change = default;
            return false;
        }

        change = conditionQueue[conditionRead];
        conditionRead = (conditionRead + 1) % ConditionQueueCapacity;
        return true;
    }

    private long EvaluateSoftware(EffectDefinition definition, long elapsed)
    {
        EffectCommon common = definition.Common;
        long local = elapsed;
        if (common.DurationMicroseconds != EffectCommon.InfiniteDuration && common.DurationMicroseconds > 0)
        {
            local %= common.DurationMicroseconds;
        }

        if (common.SamplePeriodMicroseconds > 0)
        {
            local = local / common.SamplePeriodMicroseconds * common.SamplePeriodMicroseconds;
        }

        long raw = definition switch
        {
            ConstantEffectDefinition constant => constant.Magnitude,
            RampEffectDefinition ramp => EvaluateRamp(ramp, local),
            PeriodicEffectDefinition periodic => EvaluatePeriodic(periodic, local),
            CustomEffectDefinition custom => EvaluateCustom(custom, local),
            _ => 0,
        };

        raw = Scale(raw, EnvelopeLevel(common, local));
        raw = Scale(raw, common.Gain);
        raw = Scale(raw, common.Direction);
        if (definition is PeriodicEffectDefinition or CustomEffectDefinition)
        {
            raw = Scale(raw, settings.PeriodicGain);
        }

        return raw;
    }

    private static long EvaluateRamp(RampEffectDefinition ramp, long local)
    {
        uint duration = ramp.Common.DurationMicroseconds;
        if (duration is 0 or EffectCommon.InfiniteDuration)
        {
            return ramp.Start;
        }

        return ramp.Start + ((long)ramp.End - ramp.Start) * local / duration;
    }

    private static long EvaluatePeriodic(PeriodicEffectDefinition periodic, long local)
    {
        ulong phase = ((ulong)(periodic.PhaseHundredthsOfDegree % 36_000) << 32) / 36_000;
        ulong cycleMicroseconds = (ulong)local % periodic.PeriodMicroseconds;
        phase += (cycleMicroseconds << 32) / periodic.PeriodMicroseconds;
        uint turn = (uint)phase;
        int unit = periodic.Waveform switch
        {
            ForceEffectKind.Square => turn < 0x8000_0000u ? 32_767 : -32_767,
            ForceEffectKind.Sine => SineTable[turn >> (32 - SineTableBits)],
            ForceEffectKind.Triangle => Triangle(turn),
            ForceEffectKind.SawtoothUp => (int)((long)(turn >> 16) * 65_534 / 65_535 - 32_767),
            ForceEffectKind.SawtoothDown => (int)(32_767 - (long)(turn >> 16) * 65_534 / 65_535),
            _ => 0,
        };
        return periodic.Offset + (long)periodic.Magnitude * unit / 32_767;
    }

    private static int Triangle(uint turn)
    {
        uint quadrant = turn >> 30;
        uint within = (turn >> 15) & 0x7FFF;
        return quadrant switch
        {
            0 => 32_767 - (int)(within * 32_767u / 0x8000u),
            1 => -(int)(within * 32_767u / 0x8000u),
            2 => -32_767 + (int)(within * 32_767u / 0x8000u),
            _ => (int)(within * 32_767u / 0x8000u),
        };
    }

    private static long EvaluateCustom(CustomEffectDefinition custom, long local)
    {
        ReadOnlySpan<int> samples = custom.Samples.Span;
        uint samplePeriod = custom.Common.SamplePeriodMicroseconds;
        if (samplePeriod == 0)
        {
            uint duration = custom.Common.DurationMicroseconds;
            samplePeriod = duration is 0 or EffectCommon.InfiniteDuration
                ? 1
                : Math.Max(1, duration / (uint)samples.Length);
        }

        int index = (int)((ulong)local / samplePeriod % (uint)samples.Length);
        return samples[index];
    }

    private static int EnvelopeLevel(EffectCommon common, long local)
    {
        if (common.Envelope is not { } envelope)
        {
            return 10_000;
        }

        if (envelope.AttackTimeMicroseconds > 0 && local < envelope.AttackTimeMicroseconds)
        {
            return envelope.AttackLevel + (int)((10_000L - envelope.AttackLevel) * local / envelope.AttackTimeMicroseconds);
        }

        uint duration = common.DurationMicroseconds;
        if (duration != EffectCommon.InfiniteDuration && envelope.FadeTimeMicroseconds > 0 &&
            local >= duration - Math.Min(duration, envelope.FadeTimeMicroseconds))
        {
            long fadeStart = duration - Math.Min(duration, envelope.FadeTimeMicroseconds);
            return 10_000 + (int)((long)(envelope.FadeLevel - 10_000) * (local - fadeStart) /
                Math.Min(duration, envelope.FadeTimeMicroseconds));
        }

        return 10_000;
    }

    private static long Scale(long value, int gain) => value * gain / 10_000;

    private void StopEntry(int index, Entry entry)
    {
        ReleaseCondition(index, entry);
        entry.State = EffectPlaybackState.Stopped;
    }

    private void CompleteEntry(int index, Entry entry)
    {
        ReleaseCondition(index, entry);
        entry.State = EffectPlaybackState.Completed;
    }

    private void ReleaseCondition(int index, Entry entry)
    {
        if (entry.ConditionSlot < 0)
        {
            return;
        }

        int slot = entry.ConditionSlot;
        DeactivateCondition(entry);
        conditionOwners[slot - 1] = -1;
        entry.ConditionSlot = -1;
    }

    private void ActivateCondition(Entry entry)
    {
        if (entry.ConditionSlot < 0 || entry.ConditionStarted)
        {
            return;
        }

        entry.ConditionStarted = true;
        EnqueueCondition(new ConditionSlotChange(
            ConditionChangeKind.Start,
            entry.ConditionSlot,
            entry.Handle,
            PrepareConditionDefinition((ConditionEffectDefinition)entry.Definition)));
    }

    private void DeactivateCondition(Entry entry)
    {
        if (entry.ConditionSlot < 0 || !entry.ConditionStarted)
        {
            return;
        }

        entry.ConditionStarted = false;
        EnqueueCondition(new ConditionSlotChange(ConditionChangeKind.Stop, entry.ConditionSlot, entry.Handle, null));
    }

    private void RefreshActiveConditions()
    {
        foreach (Entry? entry in entries)
        {
            if (entry is { ConditionStarted: true })
            {
                EnqueueCondition(new ConditionSlotChange(
                    ConditionChangeKind.Update,
                    entry.ConditionSlot,
                    entry.Handle,
                    PrepareConditionDefinition((ConditionEffectDefinition)entry.Definition)));
            }
        }
    }

    private ConditionEffectDefinition PrepareConditionDefinition(ConditionEffectDefinition condition)
    {
        int classGain = condition.Kind switch
        {
            ForceEffectKind.Spring => settings.SpringGain,
            ForceEffectKind.Friction => settings.FrictionGain,
            ForceEffectKind.Damper or ForceEffectKind.Inertia => settings.DamperGain,
            _ => 10_000,
        };
        long signedGain = condition.Common.Direction;
        signedGain = Scale(signedGain, condition.Common.Gain);
        signedGain = Scale(signedGain, classGain);
        signedGain = Scale(signedGain, gameGain);
        signedGain = Scale(signedGain, settings.MasterGain);
        int magnitudeGain = (int)Math.Abs(signedGain);
        var common = condition.Common with { Gain = 10_000, Direction = 10_000 };
        return condition with
        {
            Common = common,
            PositiveCoefficient = (int)Scale(condition.PositiveCoefficient, (int)signedGain),
            NegativeCoefficient = (int)Scale(condition.NegativeCoefficient, (int)signedGain),
            PositiveSaturation = (int)Scale(condition.PositiveSaturation, magnitudeGain),
            NegativeSaturation = (int)Scale(condition.NegativeSaturation, magnitudeGain),
        };
    }

    private void EnqueueCondition(ConditionSlotChange change)
    {
        int next = (conditionWrite + 1) % ConditionQueueCapacity;
        if (next == conditionRead)
        {
            throw new InvalidOperationException("Condition change queue capacity was exceeded.");
        }

        conditionQueue[conditionWrite] = change;
        conditionWrite = next;
    }

    private int FindFreeEntry()
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is null)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindEntry(uint handle)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i]?.Handle == handle)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindFreeConditionOwner()
    {
        for (int i = 0; i < conditionOwners.Length; i++)
        {
            if (conditionOwners[i] < 0)
            {
                return i;
            }
        }

        return -1;
    }

    private uint AllocateHandle()
    {
        uint first = nextHandle;
        do
        {
            uint candidate = nextHandle++;
            if (nextHandle == 0)
            {
                nextHandle = 1;
            }

            if (candidate != 0 && FindEntry(candidate) < 0)
            {
                return candidate;
            }
        }
        while (nextHandle != first);

        throw new InvalidOperationException("No effect handle is available.");
    }

    private static bool IsCondition(ForceEffectKind kind) =>
        kind is ForceEffectKind.Spring or ForceEffectKind.Damper or ForceEffectKind.Friction or ForceEffectKind.Inertia;

    private static short[] BuildSineTable()
    {
        var table = new short[SineTableLength];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (short)Math.Round(Math.Sin(2 * Math.PI * i / table.Length) * 32_767);
        }

        return table;
    }

    private sealed class Entry
    {
        public Entry(uint handle, EffectDefinition definition)
        {
            Handle = handle;
            Definition = definition;
        }

        public uint Handle { get; }

        public EffectDefinition Definition { get; set; }

        public EffectPlaybackState State { get; set; } = EffectPlaybackState.Downloaded;

        public EffectPlaybackState StateBeforePause { get; set; }

        public long StartMicroseconds { get; set; }

        public uint Iterations { get; set; }

        public int ConditionSlot { get; set; } = -1;

        public bool ConditionStarted { get; set; }
    }
}
