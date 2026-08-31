// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker;

public readonly record struct ProviderDeviceState(
    bool Paused,
    bool ActuatorsEnabled,
    bool HasActiveEffects,
    int DownloadedEffectCount);

public readonly record struct GlobalBrokerStatus(
    ulong OwnerSessionId,
    int SessionCount,
    int DownloadedEffectCount,
    bool HasActiveEffects);

public sealed class BrokerSessionCoordinator : IRuntimeMixer
{
    public const int MaximumSessions = 32;
    public const long HeartbeatIntervalMicroseconds = 100_000;
    public const long OwnerLeaseMicroseconds = 350_000;

    private readonly IMonotonicClock clock;
    private readonly Session?[] sessions = new Session[MaximumSessions];
    private readonly Drain[] drains = new Drain[MaximumSessions];
    private int drainRead;
    private int drainWrite;
    private int ownerIndex = -1;
    private ulong nextSessionId = 1;
    private RuntimeSettings runtimeSettings = RuntimeSettings.Default;
    private int rangeLockState;
    private bool stopAllBarrierPending;

    public BrokerSessionCoordinator(IMonotonicClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ulong OwnerSessionId => ownerIndex < 0 ? 0 : sessions[ownerIndex]!.Id;

    public RuntimeSettings RuntimeSettings => runtimeSettings;

    public bool HasActiveEffects => ownerIndex >= 0 && sessions[ownerIndex]!.Engine.HasActiveEffects;

    public BrokerResult OpenSession(out ulong sessionId)
    {
        sessionId = 0;
        int index = FindFreeSession();
        if (index < 0)
        {
            return BrokerResult.DeviceFull;
        }

        ulong id = AllocateSessionId();
        var engine = new EffectEngine(clock);
        _ = engine.SetRuntimeSettings(runtimeSettings);
        sessions[index] = new Session(id, engine, clock.GetMicroseconds());
        sessionId = id;
        return BrokerResult.Ok;
    }

    public BrokerResult Heartbeat(ulong sessionId)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        sessions[index]!.LastHeartbeatMicroseconds = clock.GetMicroseconds();
        return BrokerResult.Ok;
    }

    public BrokerResult CloseSession(ulong sessionId)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        Session session = sessions[index]!;
        session.Engine.Reset();
        if (index == ownerIndex)
        {
            stopAllBarrierPending = true;
            ReleaseOwner(index, clearSessionAfterDrain: true);
        }
        else
        {
            sessions[index] = null;
        }

        return BrokerResult.Ok;
    }

    public BrokerResult UpsertEffect(
        ulong sessionId,
        uint handle,
        EffectUpdateMask updateMask,
        EffectDefinition definition,
        bool validateOnly,
        out uint assignedHandle)
    {
        assignedHandle = 0;
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        EffectEngine engine = sessions[index]!.Engine;
        EffectDefinition effective = definition;
        if (handle == 0)
        {
            if (updateMask != EffectUpdateMask.All)
            {
                return BrokerResult.InvalidArgument;
            }
        }
        else
        {
            if (!engine.TryGetDefinition(handle, out EffectDefinition? existing) || existing is null)
            {
                return BrokerResult.NotFound;
            }

            if (!TryMerge(existing, definition, updateMask, out effective))
            {
                return BrokerResult.InvalidArgument;
            }
        }

        return Map(engine.Upsert(handle, effective, validateOnly, out assignedHandle));
    }

    public BrokerResult StartEffect(ulong sessionId, uint handle, uint iterations, bool solo, bool restart)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        if (ownerIndex >= 0 && ownerIndex != index)
        {
            return BrokerResult.OtherApplicationHasPriority;
        }

        bool claimed = ownerIndex < 0;
        if (claimed)
        {
            ownerIndex = index;
        }

        EngineResult result = sessions[index]!.Engine.Start(handle, iterations, solo, restart);
        if (result != EngineResult.Ok && claimed)
        {
            ownerIndex = -1;
        }

        return Map(result);
    }

    public BrokerResult StopEffect(ulong sessionId, uint handle)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        BrokerResult result = Map(sessions[index]!.Engine.Stop(handle));
        ReleaseIfIdle(index);
        return result;
    }

    public BrokerResult DestroyEffect(ulong sessionId, uint handle)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        BrokerResult result = Map(sessions[index]!.Engine.Destroy(handle));
        ReleaseIfIdle(index);
        return result;
    }

    public BrokerResult QueryEffect(ulong sessionId, uint handle, out EffectDefinition? definition, out EffectPlaybackState state)
    {
        definition = null;
        state = default;
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        EffectEngine engine = sessions[index]!.Engine;
        return engine.TryGetDefinition(handle, out definition) && engine.TryGetState(handle, out state)
            ? BrokerResult.Ok
            : BrokerResult.NotFound;
    }

    public BrokerResult SetGain(ulong sessionId, int gain)
    {
        int index = FindSession(sessionId);
        return index < 0 ? BrokerResult.InputLost : Map(sessions[index]!.Engine.SetGameGain(gain));
    }

    public BrokerResult QueryDeviceState(ulong sessionId, out ProviderDeviceState state)
    {
        state = default;
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        EffectEngine engine = sessions[index]!.Engine;
        state = new ProviderDeviceState(
            engine.IsPaused,
            engine.ActuatorsEnabled,
            engine.HasActiveEffects,
            engine.DownloadedEffectCount);
        return BrokerResult.Ok;
    }

    public BrokerResult DeviceCommand(ulong sessionId, DeviceForceCommand command)
    {
        int index = FindSession(sessionId);
        if (index < 0)
        {
            return BrokerResult.InputLost;
        }

        EffectEngine engine = sessions[index]!.Engine;
        switch (command)
        {
            case DeviceForceCommand.Pause:
                engine.Pause();
                break;
            case DeviceForceCommand.Continue:
                engine.Continue();
                break;
            case DeviceForceCommand.ActuatorsOn:
                engine.SetActuatorsEnabled(true);
                break;
            case DeviceForceCommand.ActuatorsOff:
                engine.SetActuatorsEnabled(false);
                break;
            case DeviceForceCommand.StopAll:
            case DeviceForceCommand.EmergencyStop:
                engine.StopAll();
                stopAllBarrierPending = true;
                ReleaseIfIdle(index);
                break;
            case DeviceForceCommand.Reset:
                engine.Reset();
                stopAllBarrierPending = true;
                ReleaseIfIdle(index);
                break;
            default:
                return BrokerResult.InvalidArgument;
        }

        return BrokerResult.Ok;
    }

    public BrokerResult SetRuntimeSettings(ulong sessionId, RuntimeSettings settings)
    {
        int index = FindSession(sessionId);
        return index < 0 ? BrokerResult.InputLost : Map(sessions[index]!.Engine.SetRuntimeSettings(settings));
    }

    public BrokerResult SetRuntimeSettings(RuntimeSettings settings)
    {
        if (!EffectDefinitionValidator.TryValidate(settings, out _))
        {
            return BrokerResult.InvalidArgument;
        }

        runtimeSettings = settings;
        if (settings.RangeDegrees >= 900)
        {
            rangeLockState = 0;
        }
        foreach (Session? session in sessions)
        {
            if (session is not null)
            {
                _ = session.Engine.SetRuntimeSettings(settings);
            }
        }

        return BrokerResult.Ok;
    }

    public BrokerResult ObserveSteeringPosition(int rawPosition)
    {
        if (rawPosition is < 0 or > 16_383)
        {
            return BrokerResult.InvalidArgument;
        }

        if (runtimeSettings.RangeDegrees >= 900)
        {
            rangeLockState = 0;
        }
        else if (rangeLockState == 0)
        {
            if (rawPosition <= 256) rangeLockState = -1;
            else if (rawPosition >= 16_127) rangeLockState = 1;
        }
        else if (rangeLockState < 0 && rawPosition >= 1_024)
        {
            rangeLockState = 0;
        }
        else if (rangeLockState > 0 && rawPosition <= 15_359)
        {
            rangeLockState = 0;
        }

        return BrokerResult.Ok;
    }

    public MixerSnapshot Render()
    {
        ExpireOwnerLease();
        if (ownerIndex < 0)
        {
            return new MixerSnapshot(
                clock.GetMicroseconds(),
                rangeLockState * runtimeSettings.BoundaryForce,
                0,
                0,
                0,
                0,
                0);
        }

        int renderedOwner = ownerIndex;
        MixerSnapshot snapshot = sessions[renderedOwner]!.Engine.Render();
        if (rangeLockState != 0)
        {
            snapshot = snapshot with { SoftwareForce = rangeLockState * runtimeSettings.BoundaryForce };
        }

        ReleaseIfIdle(renderedOwner);
        return snapshot;
    }

    public bool TryDequeueConditionChange(out ConditionSlotChange change)
    {
        while (drainRead != drainWrite)
        {
            ref Drain drain = ref drains[drainRead];
            if (drain.Engine!.TryDequeueConditionChange(out change))
            {
                return true;
            }

            if (drain.ClearSessionAfterDrain && sessions[drain.SessionIndex]?.Engine == drain.Engine)
            {
                sessions[drain.SessionIndex] = null;
            }

            drain = default;
            drainRead = (drainRead + 1) % drains.Length;
        }

        if (ownerIndex >= 0 && sessions[ownerIndex]!.Engine.TryDequeueConditionChange(out change))
        {
            return true;
        }

        change = default;
        return false;
    }

    public void StopAll()
    {
        stopAllBarrierPending = true;
        if (ownerIndex < 0)
        {
            return;
        }

        int index = ownerIndex;
        sessions[index]!.Engine.StopAll();
        ReleaseOwner(index, clearSessionAfterDrain: false);
    }

    public void DeviceRemoved()
    {
        stopAllBarrierPending = true;
        int previousOwner = ownerIndex;
        for (int i = 0; i < sessions.Length; i++)
        {
            sessions[i]?.Engine.Reset();
        }

        if (previousOwner >= 0)
        {
            ReleaseOwner(previousOwner, clearSessionAfterDrain: false);
        }

        rangeLockState = 0;
    }

    public BrokerResult EmergencyStop()
    {
        stopAllBarrierPending = true;
        if (ownerIndex >= 0)
        {
            int index = ownerIndex;
            sessions[index]!.Engine.StopAll();
            ReleaseOwner(index, clearSessionAfterDrain: false);
        }

        return BrokerResult.Ok;
    }

    public GlobalBrokerStatus QueryGlobalStatus()
    {
        int sessionCount = 0;
        int downloads = 0;
        foreach (Session? session in sessions)
        {
            if (session is null)
            {
                continue;
            }

            sessionCount++;
            downloads += session.Engine.DownloadedEffectCount;
        }

        return new GlobalBrokerStatus(OwnerSessionId, sessionCount, downloads, HasActiveEffects);
    }

    public bool TryConsumeStopAllBarrier()
    {
        bool pending = stopAllBarrierPending;
        stopAllBarrierPending = false;
        return pending;
    }

    private void ExpireOwnerLease()
    {
        if (ownerIndex < 0)
        {
            return;
        }

        Session owner = sessions[ownerIndex]!;
        if (clock.GetMicroseconds() - owner.LastHeartbeatMicroseconds <= OwnerLeaseMicroseconds)
        {
            return;
        }

        int index = ownerIndex;
        owner.Engine.Reset();
        stopAllBarrierPending = true;
        ReleaseOwner(index, clearSessionAfterDrain: true);
    }

    private void ReleaseIfIdle(int index)
    {
        if (ownerIndex == index && !sessions[index]!.Engine.HasActiveEffects)
        {
            ReleaseOwner(index, clearSessionAfterDrain: false);
        }
    }

    private void ReleaseOwner(int index, bool clearSessionAfterDrain)
    {
        EffectEngine engine = sessions[index]!.Engine;
        ownerIndex = -1;
        int next = (drainWrite + 1) % drains.Length;
        if (next == drainRead)
        {
            throw new InvalidOperationException("Condition drain queue capacity was exceeded.");
        }

        drains[drainWrite] = new Drain(engine, index, clearSessionAfterDrain);
        drainWrite = next;
    }

    private static bool TryMerge(
        EffectDefinition existing,
        EffectDefinition update,
        EffectUpdateMask mask,
        out EffectDefinition merged)
    {
        merged = existing;
        if ((mask & ~EffectUpdateMask.All) != 0 || existing.Kind != update.Kind)
        {
            return false;
        }

        EffectCommon old = existing.Common;
        EffectCommon incoming = update.Common;
        uint axisIdentifier = mask.HasFlag(EffectUpdateMask.Axis) ? incoming.AxisIdentifier : old.AxisIdentifier;
        if (axisIdentifier != old.AxisIdentifier)
        {
            return false;
        }

        var common = new EffectCommon(
            mask.HasFlag(EffectUpdateMask.Duration) ? incoming.DurationMicroseconds : old.DurationMicroseconds,
            mask.HasFlag(EffectUpdateMask.StartDelay) ? incoming.StartDelayMicroseconds : old.StartDelayMicroseconds,
            mask.HasFlag(EffectUpdateMask.SamplePeriod) ? incoming.SamplePeriodMicroseconds : old.SamplePeriodMicroseconds,
            mask.HasFlag(EffectUpdateMask.Gain) ? incoming.Gain : old.Gain,
            mask.HasFlag(EffectUpdateMask.Direction) ? incoming.Direction : old.Direction,
            mask.HasFlag(EffectUpdateMask.Envelope) ? incoming.Envelope : old.Envelope,
            axisIdentifier);
        EffectDefinition source = mask.HasFlag(EffectUpdateMask.TypeSpecific) ? update : existing;
        merged = source switch
        {
            ConstantEffectDefinition value => value with { Common = common },
            RampEffectDefinition value => value with { Common = common },
            PeriodicEffectDefinition value => value with { Common = common },
            ConditionEffectDefinition value => value with { Common = common },
            CustomEffectDefinition value => new CustomEffectDefinition(common, value.Samples.Span),
            _ => existing,
        };
        return EffectDefinitionValidator.TryValidate(merged, out _);
    }

    private int FindSession(ulong id)
    {
        if (id == 0)
        {
            return -1;
        }

        for (int i = 0; i < sessions.Length; i++)
        {
            if (sessions[i]?.Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindFreeSession()
    {
        for (int i = 0; i < sessions.Length; i++)
        {
            if (sessions[i] is null)
            {
                return i;
            }
        }

        return -1;
    }

    private ulong AllocateSessionId()
    {
        ulong first = nextSessionId;
        do
        {
            ulong candidate = nextSessionId++;
            if (nextSessionId == 0)
            {
                nextSessionId = 1;
            }

            if (candidate != 0 && FindSession(candidate) < 0)
            {
                return candidate;
            }
        }
        while (nextSessionId != first);

        throw new InvalidOperationException("No session identifier is available.");
    }

    private static BrokerResult Map(EngineResult result) => result switch
    {
        EngineResult.Ok => BrokerResult.Ok,
        EngineResult.InvalidArgument => BrokerResult.InvalidArgument,
        EngineResult.DeviceFull => BrokerResult.DeviceFull,
        EngineResult.NotFound or EngineResult.StaleHandle => BrokerResult.NotFound,
        _ => BrokerResult.InternalError,
    };

    private sealed class Session(ulong id, EffectEngine engine, long lastHeartbeatMicroseconds)
    {
        public ulong Id { get; } = id;

        public EffectEngine Engine { get; } = engine;

        public long LastHeartbeatMicroseconds { get; set; } = lastHeartbeatMicroseconds;
    }

    private readonly record struct Drain(EffectEngine? Engine, int SessionIndex, bool ClearSessionAfterDrain);
}
