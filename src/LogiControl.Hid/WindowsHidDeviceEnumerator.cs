// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LogiControl.Hid;

public sealed class WindowsHidDeviceEnumerator : IHidDeviceEnumerator
{
    private const int HidpStatusSuccess = 0x00110000;
    private const uint DevPropTypeGuid = 0x0000000D;
    private const uint DevPropTypeString = 0x00000012;
    private const uint DevPropTypeStringList = 0x00002012;

    private static readonly NativeMethods.DevPropKey ContainerIdKey =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly NativeMethods.DevPropKey LocationPathsKey =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 37);
    private static readonly NativeMethods.DevPropKey BusReportedDescriptionKey =
        new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);
    private static readonly NativeMethods.DevPropKey ServiceKey =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 6);
    private static readonly NativeMethods.DevPropKey DriverInfPathKey =
        new(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 5);
    private static readonly NativeMethods.DevPropKey DriverProviderKey =
        new(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 9);

    public ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<HidDeviceSnapshot>>(Enumerate(cancellationToken));
    }

    private static unsafe IReadOnlyList<HidDeviceSnapshot> Enumerate(
        CancellationToken cancellationToken)
    {
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr rawDevices = NativeMethods.SetupDiGetClassDevs(
            in hidGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
        using var devices = new SafeDeviceInfoSetHandle(rawDevices);

        if (devices.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate HID interfaces.");
        }

        var snapshots = new List<HidDeviceSnapshot>();
        for (uint index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var interfaceData = new NativeMethods.SpDeviceInterfaceData
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>(),
            };

            if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                    devices,
                    IntPtr.Zero,
                    in hidGuid,
                    index,
                    ref interfaceData))
            {
                int error = Marshal.GetLastWin32Error();
                if ((uint)error == NativeMethods.ErrorNoMoreItems)
                {
                    break;
                }

                continue;
            }

            NativeMethods.SetupDiGetDeviceInterfaceDetail(
                devices,
                ref interfaceData,
                null,
                0,
                out uint requiredSize,
                null);
            if (requiredSize == 0 ||
                (uint)Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer)
            {
                continue;
            }

            byte[] detailBuffer = new byte[requiredSize];
            var deviceInfo = new NativeMethods.SpDevInfoData
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.SpDevInfoData>(),
            };

            fixed (byte* detail = detailBuffer)
            {
                Marshal.WriteInt32((IntPtr)detail, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                        devices,
                        ref interfaceData,
                        detail,
                        requiredSize,
                        out _,
                        &deviceInfo))
                {
                    continue;
                }

                string? devicePath = Marshal.PtrToStringUni((IntPtr)(detail + sizeof(uint)));
                if (string.IsNullOrWhiteSpace(devicePath))
                {
                    continue;
                }

                HidDeviceSnapshot? snapshot = TryReadSnapshot(devices, ref deviceInfo, devicePath);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
        }

        return snapshots;
    }

    private static HidDeviceSnapshot? TryReadSnapshot(
        SafeDeviceInfoSetHandle devices,
        ref NativeMethods.SpDevInfoData deviceInfo,
        string devicePath)
    {
        using SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new NativeMethods.HiddAttributes
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes) ||
            !NativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsedData))
        {
            return null;
        }

        NativeMethods.HidpCaps capabilities;
        try
        {
            if (NativeMethods.HidP_GetCaps(preparsedData, out capabilities) != HidpStatusSuccess)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsedData);
        }

        string instanceId = ReadInstanceId(devices, ref deviceInfo);
        uint deviceInstance = deviceInfo.DevInst;
        uint? parentInstance = NativeMethods.CmGetParent(out uint parent, deviceInstance, 0) == NativeMethods.CrSuccess
            ? parent
            : null;
        Guid? containerId = ReadGuidProperty(deviceInstance, ContainerIdKey) ??
            (parentInstance is uint parentValue ? ReadGuidProperty(parentValue, ContainerIdKey) : null);
        IReadOnlyList<string> locationPaths = ReadStringListProperty(deviceInstance, LocationPathsKey);
        if (locationPaths.Count == 0 && parentInstance is uint locationParent)
        {
            locationPaths = ReadStringListProperty(locationParent, LocationPathsKey);
        }

        return new HidDeviceSnapshot(
            devicePath,
            instanceId,
            attributes.VendorId,
            attributes.ProductId,
            attributes.VersionNumber,
            capabilities.UsagePage,
            capabilities.Usage,
            capabilities.InputReportByteLength,
            capabilities.OutputReportByteLength,
            capabilities.FeatureReportByteLength,
            containerId,
            locationPaths,
            parentInstance is uint idParent ? ReadDeviceInstanceId(idParent) : null,
            ReadStringPropertyWithParent(deviceInstance, parentInstance, BusReportedDescriptionKey),
            ReadStringPropertyWithParent(deviceInstance, parentInstance, ServiceKey),
            ReadStringPropertyWithParent(deviceInstance, parentInstance, DriverProviderKey),
            ReadStringPropertyWithParent(deviceInstance, parentInstance, DriverInfPathKey));
    }

    private static string ReadInstanceId(
        SafeDeviceInfoSetHandle devices,
        ref NativeMethods.SpDevInfoData deviceInfo)
    {
        var buffer = new char[512];
        return NativeMethods.SetupDiGetDeviceInstanceId(
            devices,
            ref deviceInfo,
            buffer,
            (uint)buffer.Length,
            out _)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is int end && end >= 0 ? end : buffer.Length)
            : string.Empty;
    }

    private static string? ReadDeviceInstanceId(uint deviceInstance)
    {
        if (NativeMethods.CmGetDeviceIdSize(out uint length, deviceInstance, 0) != NativeMethods.CrSuccess)
        {
            return null;
        }

        var buffer = new char[length + 1];
        return NativeMethods.CmGetDeviceId(deviceInstance, buffer, (uint)buffer.Length, 0) == NativeMethods.CrSuccess
            ? new string(buffer, 0, (int)length)
            : null;
    }

    private static string? ReadStringPropertyWithParent(
        uint deviceInstance,
        uint? parentInstance,
        NativeMethods.DevPropKey key) =>
        ReadStringProperty(deviceInstance, key) ??
        (parentInstance is uint parent ? ReadStringProperty(parent, key) : null);

    private static Guid? ReadGuidProperty(uint deviceInstance, NativeMethods.DevPropKey key)
    {
        byte[]? bytes = ReadProperty(deviceInstance, key, out uint propertyType);
        return bytes is { Length: >= 16 } && propertyType == DevPropTypeGuid
            ? new Guid(bytes.AsSpan(0, 16))
            : null;
    }

    private static string? ReadStringProperty(uint deviceInstance, NativeMethods.DevPropKey key)
    {
        byte[]? bytes = ReadProperty(deviceInstance, key, out uint propertyType);
        if (bytes is null || propertyType != DevPropTypeString)
        {
            return null;
        }

        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static IReadOnlyList<string> ReadStringListProperty(
        uint deviceInstance,
        NativeMethods.DevPropKey key)
    {
        byte[]? bytes = ReadProperty(deviceInstance, key, out uint propertyType);
        if (bytes is null || propertyType != DevPropTypeStringList)
        {
            return [];
        }

        return Encoding.Unicode.GetString(bytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static unsafe byte[]? ReadProperty(
        uint deviceInstance,
        NativeMethods.DevPropKey key,
        out uint propertyType)
    {
        uint size = 0;
        int result = NativeMethods.CmGetDevNodeProperty(
            deviceInstance,
            in key,
            out propertyType,
            null,
            ref size,
            0);
        if (result != NativeMethods.CrBufferSmall || size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            result = NativeMethods.CmGetDevNodeProperty(
                deviceInstance,
                in key,
                out propertyType,
                data,
                ref size,
                0);
        }

        return result == NativeMethods.CrSuccess ? buffer : null;
    }
}
