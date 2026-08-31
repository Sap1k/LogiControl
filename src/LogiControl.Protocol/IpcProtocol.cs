// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace LogiControl.Protocol;

public enum IpcMessageType : ushort
{
    Hello = 1,
    BindDevice = 2,
    Heartbeat = 3,
    CloseSession = 4,
    ValidateEffect = 10,
    UpsertEffect = 11,
    StartEffect = 12,
    StopEffect = 13,
    DestroyEffect = 14,
    QueryEffect = 15,
    SetGain = 16,
    DeviceCommand = 17,
    QueryDeviceState = 18,
    SetRuntimeSettings = 30,
    QueryRuntimeSettings = 31,
    QueryStatus = 32,
    QueryTelemetry = 33,
    EmergencyStop = 34,
}

[Flags]
public enum IpcFrameFlags : ushort
{
    None = 0,
    Response = 1,
    Error = 2,
}

public enum BrokerResult : int
{
    Ok = 0,
    InvalidArgument = 1,
    Unsupported = 2,
    DeviceFull = 3,
    OtherApplicationHasPriority = 4,
    InputLost = 5,
    NotFound = 6,
    DeviceNotReady = 7,
    ProtocolError = 8,
    InternalError = 9,
}

public readonly record struct IpcFrameHeader(
    ushort MajorVersion,
    ushort MinorVersion,
    IpcMessageType MessageType,
    IpcFrameFlags Flags,
    uint PayloadLength,
    ulong RequestId,
    ulong SessionId);

public static class IpcFrameCodec
{
    public const uint Magic = 0x4646434C;
    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 0;
    public const int HeaderLength = 32;
    public const int MaximumFrameLength = 64 * 1024;
    public const int MaximumPayloadLength = MaximumFrameLength - HeaderLength;

    public static bool TryWriteHeader(Span<byte> destination, IpcFrameHeader header)
    {
        if (destination.Length < HeaderLength || header.PayloadLength > MaximumPayloadLength)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], header.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], header.MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)header.MessageType);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], (ushort)header.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], header.PayloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], header.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.SessionId);
        return true;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> source, out IpcFrameHeader header)
    {
        header = default;
        if (source.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
        {
            return false;
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        if (payloadLength > MaximumPayloadLength)
        {
            return false;
        }

        header = new IpcFrameHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..]),
            (IpcMessageType)BinaryPrimitives.ReadUInt16LittleEndian(source[8..]),
            (IpcFrameFlags)BinaryPrimitives.ReadUInt16LittleEndian(source[10..]),
            payloadLength,
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..]));
        return true;
    }
}
