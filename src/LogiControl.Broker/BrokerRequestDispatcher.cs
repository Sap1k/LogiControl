// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public sealed class BrokerRequestDispatcher
{
    private const uint StartSolo = 1;
    private const uint StartRestart = 2;
    private readonly BrokerSessionCoordinator coordinator;
    private readonly EffectRuntime runtime;
    private readonly Func<bool> deviceReady;
    private ulong sessionId;
    private bool bound;

    public BrokerRequestDispatcher(BrokerSessionCoordinator coordinator, EffectRuntime runtime, bool deviceReady)
        : this(coordinator, runtime, () => deviceReady)
    {
    }

    public BrokerRequestDispatcher(BrokerSessionCoordinator coordinator, EffectRuntime runtime, Func<bool> deviceReady)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.deviceReady = deviceReady ?? throw new ArgumentNullException(nameof(deviceReady));
    }

    public ulong SessionId => sessionId;

    public IpcFrame Dispatch(IpcFrame request)
    {
        IpcFrameHeader header = request.Header;
        if (header.MajorVersion != IpcFrameCodec.MajorVersion || header.MinorVersion > IpcFrameCodec.MinorVersion ||
            header.Flags != IpcFrameFlags.None || header.RequestId == 0 || header.PayloadLength != request.Payload.Length)
        {
            return Response(header, BrokerResult.ProtocolError);
        }

        if (header.MessageType == IpcMessageType.Hello)
        {
            if (sessionId != 0 || header.SessionId != 0 || request.Payload.Length != 0)
            {
                return Response(header, BrokerResult.ProtocolError);
            }

            BrokerResult opened = runtime.Invoke(() => coordinator.OpenSession(out sessionId), TimeSpan.FromMilliseconds(250));
            return Response(header, opened, sessionOverride: sessionId);
        }

        if (sessionId == 0 || header.SessionId != sessionId)
        {
            return Response(header, BrokerResult.InputLost);
        }

        return header.MessageType switch
        {
            IpcMessageType.BindDevice => BindDevice(header, request.Payload),
            IpcMessageType.Heartbeat => NoPayload(header, request.Payload,
                () => coordinator.Heartbeat(sessionId)),
            IpcMessageType.CloseSession => Close(header, request.Payload),
            IpcMessageType.ValidateEffect => ValidateEffect(header, request.Payload),
            IpcMessageType.UpsertEffect => UpsertEffect(header, request.Payload),
            IpcMessageType.StartEffect => StartEffect(header, request.Payload),
            IpcMessageType.StopEffect => HandleCommand(header, request.Payload, coordinator.StopEffect),
            IpcMessageType.DestroyEffect => HandleCommand(header, request.Payload, coordinator.DestroyEffect),
            IpcMessageType.QueryEffect => QueryEffect(header, request.Payload),
            IpcMessageType.SetGain => SetGain(header, request.Payload),
            IpcMessageType.DeviceCommand => DeviceCommand(header, request.Payload),
            IpcMessageType.QueryDeviceState => QueryDeviceState(header, request.Payload),
            IpcMessageType.SetRuntimeSettings => SetRuntimeSettings(header, request.Payload),
            IpcMessageType.QueryRuntimeSettings => QueryRuntimeSettings(header, request.Payload),
            IpcMessageType.QueryStatus => QueryStatus(header, request.Payload),
            IpcMessageType.QueryTelemetry => QueryTelemetry(header, request.Payload),
            IpcMessageType.EmergencyStop => EmergencyStop(header, request.Payload),
            _ => Response(header, BrokerResult.Unsupported),
        };
    }

    public void CloseAfterTransportLoss()
    {
        if (sessionId == 0)
        {
            return;
        }

        ulong closing = sessionId;
        sessionId = 0;
        bound = false;
        try
        {
            _ = runtime.Invoke(() => coordinator.CloseSession(closing), TimeSpan.FromMilliseconds(250));
        }
        catch (TimeoutException)
        {
        }
    }

    private IpcFrame BindDevice(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length is < 2 or > 514)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (length == 0 || length != payload.Length - 2)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        bound = deviceReady();
        return Response(header, bound ? BrokerResult.Ok : BrokerResult.DeviceNotReady);
    }

    private IpcFrame ValidateEffect(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)) != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var mask = (EffectUpdateMask)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
        if (!EffectDefinitionCodec.TryRead(payload.AsSpan(8), out EffectDefinition? definition,
                validate: handle == 0 || mask == EffectUpdateMask.All) || definition is null)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        BrokerResult result = runtime.Invoke(
            () => coordinator.UpsertEffect(sessionId, handle, mask, definition, true, out _),
            TimeSpan.FromMilliseconds(250));
        return Response(header, result);
    }

    private IpcFrame UpsertEffect(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)) != 0 ||
            !EffectDefinitionCodec.TryRead(payload.AsSpan(8), out EffectDefinition? definition,
                validate: BinaryPrimitives.ReadUInt32LittleEndian(payload) == 0 ||
                    (EffectUpdateMask)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)) == EffectUpdateMask.All) ||
            definition is null)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var mask = (EffectUpdateMask)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
        uint assigned = 0;
        BrokerResult result = runtime.Invoke(
            () => coordinator.UpsertEffect(sessionId, handle, mask, definition, false, out assigned),
            TimeSpan.FromMilliseconds(250));
        Span<byte> extra = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(extra, assigned);
        return Response(header, result, extra);
    }

    private IpcFrame StartEffect(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 12)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        if (!bound || !deviceReady())
        {
            return Response(header, BrokerResult.DeviceNotReady);
        }

        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint iterations = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8));
        if ((flags & ~(StartSolo | StartRestart)) != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        BrokerResult result = runtime.Invoke(
            () => coordinator.StartEffect(sessionId, handle, iterations, (flags & StartSolo) != 0, (flags & StartRestart) != 0),
            TimeSpan.FromMilliseconds(250));
        return Response(header, result);
    }

    private IpcFrame HandleCommand(
        IpcFrameHeader header,
        byte[] payload,
        Func<ulong, uint, BrokerResult> operation)
    {
        if (payload.Length != 4)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return Response(header, runtime.Invoke(() => operation(sessionId, handle), TimeSpan.FromMilliseconds(250)));
    }

    private IpcFrame QueryEffect(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 4)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        EffectDefinition? definition = null;
        EffectPlaybackState state = default;
        BrokerResult result = runtime.Invoke(
            () => coordinator.QueryEffect(sessionId, BinaryPrimitives.ReadUInt32LittleEndian(payload), out definition, out state),
            TimeSpan.FromMilliseconds(250));
        if (result != BrokerResult.Ok || definition is null)
        {
            return Response(header, result);
        }

        int effectLength = EffectDefinitionCodec.GetEncodedLength(definition);
        var extra = new byte[4 + effectLength];
        extra[0] = (byte)state;
        if (!EffectDefinitionCodec.TryWrite(extra.AsSpan(4), definition, out _))
        {
            return Response(header, BrokerResult.InternalError);
        }

        return Response(header, result, extra);
    }

    private IpcFrame SetGain(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 4)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        int gain = BinaryPrimitives.ReadInt32LittleEndian(payload);
        return Response(header, runtime.Invoke(() => coordinator.SetGain(sessionId, gain), TimeSpan.FromMilliseconds(250)));
    }

    private IpcFrame DeviceCommand(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 4 || payload[1] != 0 || payload[2] != 0 || payload[3] != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        var command = (DeviceForceCommand)payload[0];
        return Response(header, runtime.Invoke(() => coordinator.DeviceCommand(sessionId, command), TimeSpan.FromMilliseconds(250)));
    }

    private IpcFrame QueryDeviceState(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        ulong owner = 0;
        ProviderDeviceState state = default;
        BrokerResult result = runtime.Invoke(() =>
        {
            owner = coordinator.OwnerSessionId;
            return coordinator.QueryDeviceState(sessionId, out state);
        }, TimeSpan.FromMilliseconds(250));
        if (result != BrokerResult.Ok)
        {
            return Response(header, result);
        }

        Span<byte> extra = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(extra, owner);
        BinaryPrimitives.WriteUInt64LittleEndian(extra[8..], sessionId);
        uint flags = (state.Paused ? 1u : 0u) |
            (state.ActuatorsEnabled ? 2u : 0u) |
            (state.HasActiveEffects ? 4u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(extra[16..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(extra[20..], state.DownloadedEffectCount);
        return Response(header, BrokerResult.Ok, extra);
    }

    private IpcFrame SetRuntimeSettings(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 32)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        var settings = new RuntimeSettings(
            ReadInt(payload, 0), ReadInt(payload, 4), ReadInt(payload, 8), ReadInt(payload, 12),
            ReadInt(payload, 16), ReadInt(payload, 20), ReadInt(payload, 24), ReadInt(payload, 28));
        return Response(header, runtime.Invoke(() => coordinator.SetRuntimeSettings(settings), TimeSpan.FromMilliseconds(250)));
    }

    private IpcFrame QueryRuntimeSettings(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        RuntimeSettings settings = runtime.Invoke(() => coordinator.RuntimeSettings, TimeSpan.FromMilliseconds(250));
        Span<byte> extra = stackalloc byte[32];
        WriteInt(extra, 0, settings.RangeDegrees);
        WriteInt(extra, 4, settings.MasterGain);
        WriteInt(extra, 8, settings.PeriodicGain);
        WriteInt(extra, 12, settings.SpringGain);
        WriteInt(extra, 16, settings.DamperGain);
        WriteInt(extra, 20, settings.FrictionGain);
        WriteInt(extra, 24, settings.BoundaryForce);
        WriteInt(extra, 28, settings.IdleAutocenter);
        return Response(header, BrokerResult.Ok, extra);
    }

    private IpcFrame QueryStatus(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        GlobalBrokerStatus status = runtime.Invoke(coordinator.QueryGlobalStatus, TimeSpan.FromMilliseconds(250));
        Span<byte> extra = stackalloc byte[24];
        extra.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(extra, status.OwnerSessionId);
        BinaryPrimitives.WriteInt32LittleEndian(extra[8..], status.SessionCount);
        BinaryPrimitives.WriteInt32LittleEndian(extra[12..], status.DownloadedEffectCount);
        uint flags = (deviceReady() ? 1u : 0u) | (status.HasActiveEffects ? 2u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(extra[16..], flags);
        return Response(header, BrokerResult.Ok, extra);
    }

    private IpcFrame QueryTelemetry(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        RuntimeTelemetry telemetry = runtime.Telemetry;
        Span<byte> extra = stackalloc byte[104];
        extra.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(extra, telemetry.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(extra[8..], telemetry.MissedDeadlines);
        BinaryPrimitives.WriteInt64LittleEndian(extra[16..], telemetry.Overruns);
        BinaryPrimitives.WriteInt64LittleEndian(extra[24..], telemetry.CommandCount);
        BinaryPrimitives.WriteInt64LittleEndian(extra[32..], telemetry.MixerAllocatedBytes);
        BinaryPrimitives.WriteInt64LittleEndian(extra[40..], telemetry.TicksWithAllocations);
        OutputTelemetry? output = runtime.OutputTelemetry;
        if (output is not null)
        {
            BinaryPrimitives.WriteInt64LittleEndian(extra[48..], output.DesiredPublications);
            BinaryPrimitives.WriteInt64LittleEndian(extra[56..], output.BarrierPublications);
            BinaryPrimitives.WriteInt64LittleEndian(extra[64..], output.CoalescedReports);
            BinaryPrimitives.WriteInt64LittleEndian(extra[72..], output.HidSubmissions);
            BinaryPrimitives.WriteInt64LittleEndian(extra[80..], output.HidCompletions);
            BinaryPrimitives.WriteInt64LittleEndian(extra[88..], output.WriteFailures);
            BinaryPrimitives.WriteInt64LittleEndian(extra[96..], output.MaximumQueueDepth);
        }

        return Response(header, BrokerResult.Ok, extra);
    }

    private IpcFrame EmergencyStop(IpcFrameHeader header, byte[] payload) =>
        NoPayload(header, payload, coordinator.EmergencyStop);

    private IpcFrame Close(IpcFrameHeader header, byte[] payload)
    {
        if (payload.Length != 0)
        {
            return Response(header, BrokerResult.InvalidArgument);
        }

        BrokerResult result = runtime.Invoke(() => coordinator.CloseSession(sessionId), TimeSpan.FromMilliseconds(250));
        ulong closed = sessionId;
        sessionId = 0;
        bound = false;
        return Response(header, result, sessionOverride: closed);
    }

    private IpcFrame NoPayload(IpcFrameHeader header, byte[] payload, Func<BrokerResult> operation) =>
        payload.Length == 0
            ? Response(header, runtime.Invoke(operation, TimeSpan.FromMilliseconds(250)))
            : Response(header, BrokerResult.InvalidArgument);

    private IpcFrame Response(
        IpcFrameHeader request,
        BrokerResult result,
        ReadOnlySpan<byte> extra = default,
        ulong? sessionOverride = null)
    {
        var payload = new byte[4 + extra.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, (int)result);
        extra.CopyTo(payload.AsSpan(4));
        var header = new IpcFrameHeader(
            IpcFrameCodec.MajorVersion,
            IpcFrameCodec.MinorVersion,
            request.MessageType,
            IpcFrameFlags.Response | (result == BrokerResult.Ok ? IpcFrameFlags.None : IpcFrameFlags.Error),
            (uint)payload.Length,
            request.RequestId,
            sessionOverride ?? sessionId);
        return new IpcFrame(header, payload);
    }

    private static int ReadInt(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);

    private static void WriteInt(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);
}
