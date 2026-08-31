// SPDX-License-Identifier: GPL-3.0-or-later
// Byte-compatible client for the temporary DFGT Control-derived broker protocol.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace LogiControl.DeviceAgent;

public sealed class LegacyBrokerControlClient : ILegacyBrokerControlClient
{
    public const string PipeName = "LogiControl.LegacyControl.v1";

    private const uint Magic = 0x43474644;
    private const ushort Version = 1;
    private const int ProfileIdCharacters = 64;
    private const int DevicePathCharacters = 512;
    private const int ProfileSize = ProfileIdCharacters * 2 + 7 * sizeof(int);
    private const int RequestSize = 16 + ProfileSize + DevicePathCharacters * 2;
    private const int StatusSize = 9 * sizeof(uint) + ProfileIdCharacters * 2;
    private const int ResponseSize = 16 + StatusSize;

    private readonly SemaphoreSlim requestLock = new(1, 1);
    private NamedPipeClientStream? pipe;
    private uint sequence;

    public async ValueTask<LegacyBrokerStatus> AttachAsync(
        string devicePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        return await SendAsync(3, devicePath, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LegacyBrokerStatus> ApplyProfileAsync(
        int rangeDegrees = 900,
        int overallGain = 10000,
        int boundaryForce = 3000,
        CancellationToken cancellationToken = default)
    {
        var profile = new LegacyProfile(
            "desktop",
            rangeDegrees,
            overallGain,
            boundaryForce,
            10000,
            10000,
            10000,
            10000);
        return await SendAsync(4, null, profile, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LegacyBrokerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(2, null, null, cancellationToken);

    public ValueTask<LegacyBrokerStatus> EmergencyStopAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(5, null, null, cancellationToken);

    public ValueTask<LegacyBrokerStatus> DetachAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(6, null, null, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            pipe?.Dispose();
            pipe = null;
        }
        finally
        {
            requestLock.Release();
            requestLock.Dispose();
        }
    }

    private async ValueTask<LegacyBrokerStatus> SendAsync(
        uint command,
        string? devicePath,
        LegacyProfile? profile,
        CancellationToken cancellationToken)
    {
        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NamedPipeClientStream connectedPipe = await GetConnectedPipeAsync(cancellationToken).ConfigureAwait(false);
            uint requestSequence = ++sequence;
            byte[] request = EncodeRequest(command, requestSequence, devicePath, profile);
            await connectedPipe.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await connectedPipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var response = new byte[ResponseSize];
            await ReadExactlyAsync(connectedPipe, response, cancellationToken).ConfigureAwait(false);
            return DecodeResponse(response, requestSequence);
        }
        catch
        {
            pipe?.Dispose();
            pipe = null;
            throw;
        }
        finally
        {
            requestLock.Release();
        }
    }

    private async ValueTask<NamedPipeClientStream> GetConnectedPipeAsync(CancellationToken cancellationToken)
    {
        if (pipe?.IsConnected == true)
        {
            return pipe;
        }

        pipe?.Dispose();
        pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        pipe.ReadMode = PipeTransmissionMode.Message;
        return pipe;
    }

    private static byte[] EncodeRequest(
        uint command,
        uint sequence,
        string? devicePath,
        LegacyProfile? profile)
    {
        var request = new byte[RequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6, 2), RequestSize);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8, 4), command);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12, 4), sequence);

        LegacyProfile effectiveProfile = profile ?? LegacyProfile.Default;
        WriteFixedString(request.AsSpan(16, ProfileIdCharacters * 2), effectiveProfile.Id, ProfileIdCharacters);
        int offset = 16 + ProfileIdCharacters * 2;
        foreach (int value in effectiveProfile.Values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset, 4), value);
            offset += 4;
        }

        WriteFixedString(
            request.AsSpan(16 + ProfileSize, DevicePathCharacters * 2),
            devicePath ?? string.Empty,
            DevicePathCharacters);
        return request;
    }

    private static LegacyBrokerStatus DecodeResponse(ReadOnlySpan<byte> response, uint expectedSequence)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(response[..4]) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(4, 2)) != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(6, 2)) != ResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(response.Slice(8, 4)) != expectedSequence)
        {
            throw new InvalidDataException("Legacy broker returned an invalid response header.");
        }

        int result = BinaryPrimitives.ReadInt32LittleEndian(response.Slice(12, 4));
        if (result < 0)
        {
            throw new InvalidOperationException($"Legacy broker command failed with HRESULT 0x{result:X8}.");
        }

        int offset = 16;
        LegacyBrokerDeviceState state = (LegacyBrokerDeviceState)ReadUInt32(response, ref offset);
        bool connected = ReadUInt32(response, ref offset) != 0;
        bool ffbConnected = ReadUInt32(response, ref offset) != 0;
        bool controlConnected = ReadUInt32(response, ref offset) != 0;
        uint failSafeCount = ReadUInt32(response, ref offset);
        int range = unchecked((int)ReadUInt32(response, ref offset));
        int gain = unchecked((int)ReadUInt32(response, ref offset));
        int boundary = unchecked((int)ReadUInt32(response, ref offset));
        int lastResult = unchecked((int)ReadUInt32(response, ref offset));
        string profileId = ReadFixedString(response.Slice(offset, ProfileIdCharacters * 2));

        return new LegacyBrokerStatus(
            state,
            connected,
            ffbConnected,
            controlConnected,
            failSafeCount,
            range,
            gain,
            boundary,
            lastResult,
            profileId);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static void WriteFixedString(Span<byte> destination, string value, int characters)
    {
        if (value.Length >= characters)
        {
            throw new ArgumentException($"Value exceeds the fixed field limit of {characters - 1} characters.", nameof(value));
        }

        Encoding.Unicode.GetBytes(value, destination);
    }

    private static string ReadFixedString(ReadOnlySpan<byte> source)
    {
        string value = Encoding.Unicode.GetString(source);
        int terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Legacy broker closed the control pipe.");
            }

            offset += read;
        }
    }

    private sealed record LegacyProfile(
        string Id,
        int RangeDegrees,
        int OverallGain,
        int BoundaryForce,
        int SpringGain,
        int DamperGain,
        int FrictionGain,
        int PeriodicGain)
    {
        internal static LegacyProfile Default { get; } =
            new("", 900, 10000, 3000, 10000, 10000, 10000, 10000);

        internal int[] Values =>
        [
            RangeDegrees,
            OverallGain,
            BoundaryForce,
            SpringGain,
            DamperGain,
            FrictionGain,
            PeriodicGain,
        ];
    }
}
