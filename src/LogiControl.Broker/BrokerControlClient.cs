// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Pipes;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public readonly record struct BrokerControlStatus(
    bool DeviceReady,
    bool HasActiveEffects,
    ulong OwnerSessionId,
    int SessionCount,
    int DownloadedEffectCount);

public readonly record struct BrokerTelemetryStatus(
    long Ticks,
    long MissedDeadlines,
    long Overruns,
    long Commands,
    long MixerAllocatedBytes,
    long TicksWithAllocations,
    long DesiredPublications,
    long BarrierPublications,
    long CoalescedReports,
    long HidSubmissions,
    long HidCompletions,
    long WriteFailures,
    long MaximumQueueDepth);

public sealed class BrokerControlClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe = new(
        ".",
        BrokerConstants.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
    private ulong requestId;
    private ulong sessionId;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await pipe.ConnectAsync(1_000, cancellationToken).ConfigureAwait(false);
        IpcFrame hello = await SendAsync(IpcMessageType.Hello, [], cancellationToken, sessionOverride: 0)
            .ConfigureAwait(false);
        EnsureOk(hello);
        if (hello.Header.SessionId == 0)
        {
            throw new InvalidDataException("Broker returned a zero control-session identifier.");
        }

        sessionId = hello.Header.SessionId;
    }

    public async ValueTask<BrokerControlStatus> QueryStatusAsync(CancellationToken cancellationToken = default)
    {
        IpcFrame response = await SendAsync(IpcMessageType.QueryStatus, [], cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> payload = EnsureOk(response, 24);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        return new BrokerControlStatus(
            (flags & 1) != 0,
            (flags & 2) != 0,
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[12..]));
    }

    public async ValueTask<RuntimeSettings> QueryRuntimeSettingsAsync(CancellationToken cancellationToken = default)
    {
        IpcFrame response = await SendAsync(IpcMessageType.QueryRuntimeSettings, [], cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> payload = EnsureOk(response, 32);
        return new RuntimeSettings(
            ReadInt(payload, 0), ReadInt(payload, 4), ReadInt(payload, 8), ReadInt(payload, 12),
            ReadInt(payload, 16), ReadInt(payload, 20), ReadInt(payload, 24), ReadInt(payload, 28));
    }

    public async ValueTask SetRuntimeSettingsAsync(
        RuntimeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[32];
        WriteInt(payload, 0, settings.RangeDegrees);
        WriteInt(payload, 4, settings.MasterGain);
        WriteInt(payload, 8, settings.PeriodicGain);
        WriteInt(payload, 12, settings.SpringGain);
        WriteInt(payload, 16, settings.DamperGain);
        WriteInt(payload, 20, settings.FrictionGain);
        WriteInt(payload, 24, settings.BoundaryForce);
        WriteInt(payload, 28, settings.IdleAutocenter);
        EnsureOk(await SendAsync(IpcMessageType.SetRuntimeSettings, payload, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<BrokerTelemetryStatus> QueryTelemetryAsync(CancellationToken cancellationToken = default)
    {
        IpcFrame response = await SendAsync(IpcMessageType.QueryTelemetry, [], cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> payload = EnsureOk(response, 104);
        return new BrokerTelemetryStatus(
            ReadLong(payload, 0), ReadLong(payload, 8), ReadLong(payload, 16), ReadLong(payload, 24),
            ReadLong(payload, 32), ReadLong(payload, 40), ReadLong(payload, 48), ReadLong(payload, 56),
            ReadLong(payload, 64), ReadLong(payload, 72), ReadLong(payload, 80), ReadLong(payload, 88),
            ReadLong(payload, 96));
    }

    public async ValueTask EmergencyStopAsync(CancellationToken cancellationToken = default) =>
        EnsureOk(await SendAsync(IpcMessageType.EmergencyStop, [], cancellationToken).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        if (pipe.IsConnected && sessionId != 0)
        {
            try
            {
                _ = await SendAsync(IpcMessageType.CloseSession, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
            }
        }

        sessionId = 0;
        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<IpcFrame> SendAsync(
        IpcMessageType type,
        byte[] payload,
        CancellationToken cancellationToken,
        ulong? sessionOverride = null)
    {
        ulong next = ++requestId;
        if (next == 0)
        {
            next = ++requestId;
        }

        var header = new IpcFrameHeader(
            IpcFrameCodec.MajorVersion,
            IpcFrameCodec.MinorVersion,
            type,
            IpcFrameFlags.None,
            (uint)payload.Length,
            next,
            sessionOverride ?? sessionId);
        await IpcFrameStream.WriteAsync(pipe, header, payload, cancellationToken).ConfigureAwait(false);
        IpcFrame response = await IpcFrameStream.ReadAsync(pipe, cancellationToken).ConfigureAwait(false) ??
            throw new EndOfStreamException("Broker closed the control pipe without a response.");
        if (response.Header.MajorVersion != IpcFrameCodec.MajorVersion ||
            response.Header.MinorVersion > IpcFrameCodec.MinorVersion ||
            response.Header.MessageType != type || response.Header.RequestId != next ||
            response.Header.SessionId == 0 ||
            (sessionId != 0 && response.Header.SessionId != sessionId) ||
            (response.Header.Flags & IpcFrameFlags.Response) == 0)
        {
            throw new InvalidDataException("Broker returned a mismatched semantic response.");
        }

        return response;
    }

    private static ReadOnlySpan<byte> EnsureOk(IpcFrame response, int expectedExtraLength = 0)
    {
        if (response.Payload.Length != 4 + expectedExtraLength)
        {
            throw new InvalidDataException("Broker returned an unexpected response payload length.");
        }

        var result = (BrokerResult)BinaryPrimitives.ReadInt32LittleEndian(response.Payload);
        if (result != BrokerResult.Ok)
        {
            throw new InvalidOperationException($"Broker request failed with {result}.");
        }

        return response.Payload.AsSpan(4);
    }

    private static int ReadInt(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);

    private static long ReadLong(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);

    private static void WriteInt(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);
}
