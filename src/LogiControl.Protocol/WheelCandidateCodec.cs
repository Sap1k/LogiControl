// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace LogiControl.Protocol;

public sealed record WheelCandidateInfo(
    ulong DeviceId,
    WheelModel Model,
    string DisplayName,
    ushort VersionNumber,
    ushort PresentedProductId,
    string DevicePath,
    byte LifecycleState,
    bool IsSelected,
    bool IsReady);

public static class WheelCandidateCodec
{
    public const int MaximumDisplayNameBytes = 128;
    public const int MaximumDevicePathBytes = 512;
    private const int HeaderLength = 2;
    private const int EntryHeaderLength = 20;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(IReadOnlyList<WheelCandidateInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > ClassicWheelCatalog.MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates));
        }

        var strings = new (byte[] Name, byte[] Path)[candidates.Count];
        int length = HeaderLength;
        for (int index = 0; index < candidates.Count; index++)
        {
            WheelCandidateInfo candidate = candidates[index];
            if (candidate.DeviceId == 0 || string.IsNullOrWhiteSpace(candidate.DisplayName) ||
                string.IsNullOrWhiteSpace(candidate.DevicePath))
            {
                throw new ArgumentException("Candidate identity and strings must be non-empty.", nameof(candidates));
            }

            byte[] name = StrictUtf8.GetBytes(candidate.DisplayName);
            byte[] path = StrictUtf8.GetBytes(candidate.DevicePath);
            if (name.Length > MaximumDisplayNameBytes || path.Length > MaximumDevicePathBytes)
            {
                throw new ArgumentException("Candidate strings exceed the IPC bounds.", nameof(candidates));
            }

            strings[index] = (name, path);
            length = checked(length + EntryHeaderLength + name.Length + path.Length);
        }

        var payload = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)candidates.Count);
        int offset = HeaderLength;
        for (int index = 0; index < candidates.Count; index++)
        {
            WheelCandidateInfo candidate = candidates[index];
            (byte[] name, byte[] path) = strings[index];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset), candidate.DeviceId);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 8), (ushort)candidate.Model);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 10), candidate.VersionNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 12), candidate.PresentedProductId);
            payload[offset + 14] = candidate.LifecycleState;
            payload[offset + 15] = (byte)((candidate.IsSelected ? 1 : 0) | (candidate.IsReady ? 2 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 16), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 18), (ushort)path.Length);
            offset += EntryHeaderLength;
            name.CopyTo(payload, offset);
            offset += name.Length;
            path.CopyTo(payload, offset);
            offset += path.Length;
        }

        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out IReadOnlyList<WheelCandidateInfo>? candidates)
    {
        candidates = null;
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (count > ClassicWheelCatalog.MaximumCandidates)
        {
            return false;
        }

        var decoded = new WheelCandidateInfo[count];
        int offset = HeaderLength;
        try
        {
            for (int index = 0; index < count; index++)
            {
                if (payload.Length - offset < EntryHeaderLength)
                {
                    return false;
                }

                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]);
                var model = (WheelModel)BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 8)..]);
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 10)..]);
                ushort product = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 12)..]);
                byte lifecycle = payload[offset + 14];
                byte flags = payload[offset + 15];
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 16)..]);
                int pathLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 18)..]);
                offset += EntryHeaderLength;
                if (id == 0 || (flags & ~3) != 0 || nameLength is <= 0 or > MaximumDisplayNameBytes ||
                    pathLength is <= 0 or > MaximumDevicePathBytes ||
                    payload.Length - offset < nameLength + pathLength)
                {
                    return false;
                }

                string name = StrictUtf8.GetString(payload.Slice(offset, nameLength));
                offset += nameLength;
                string path = StrictUtf8.GetString(payload.Slice(offset, pathLength));
                offset += pathLength;
                decoded[index] = new WheelCandidateInfo(
                    id, model, name, version, product, path, lifecycle, (flags & 1) != 0, (flags & 2) != 0);
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (offset != payload.Length)
        {
            return false;
        }

        candidates = decoded;
        return true;
    }
}
