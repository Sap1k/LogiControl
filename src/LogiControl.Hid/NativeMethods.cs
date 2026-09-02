// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LogiControl.Hid;

internal static partial class NativeMethods
{
    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const uint ErrorInsufficientBuffer = 122;
    internal const uint ErrorNoMoreItems = 259;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const int CrSuccess = 0;
    internal const int CrBufferSmall = 0x1A;
    internal const int CmNotifyFilterTypeDeviceInterface = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        internal uint Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        internal fixed ushort Reserved[17];
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct HidpValueCaps
    {
        [FieldOffset(0)] internal ushort UsagePage;
        [FieldOffset(2)] internal byte ReportId;
        [FieldOffset(3)] internal byte IsAlias;
        [FieldOffset(4)] internal ushort BitField;
        [FieldOffset(6)] internal ushort LinkCollection;
        [FieldOffset(8)] internal ushort LinkUsage;
        [FieldOffset(10)] internal ushort LinkUsagePage;
        [FieldOffset(12)] internal byte IsRange;
        [FieldOffset(13)] internal byte IsStringRange;
        [FieldOffset(14)] internal byte IsDesignatorRange;
        [FieldOffset(15)] internal byte IsAbsolute;
        [FieldOffset(16)] internal byte HasNull;
        [FieldOffset(18)] internal ushort BitSize;
        [FieldOffset(20)] internal ushort ReportCount;
        [FieldOffset(32)] internal uint UnitsExponent;
        [FieldOffset(36)] internal uint Units;
        [FieldOffset(40)] internal int LogicalMinimum;
        [FieldOffset(44)] internal int LogicalMaximum;
        [FieldOffset(48)] internal int PhysicalMinimum;
        [FieldOffset(52)] internal int PhysicalMaximum;
        [FieldOffset(56)] internal ushort UsageMinimum;
        [FieldOffset(58)] internal ushort UsageMaximum;
        [FieldOffset(56)] internal ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct DevPropKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CmNotifyFilter
    {
        internal uint Size;
        internal uint Flags;
        internal int FilterType;
        internal uint Reserved;
        internal Guid ClassGuid;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 384)]
        internal byte[] ReservedData;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint CmNotifyCallback(
        IntPtr notification,
        IntPtr context,
        uint action,
        IntPtr eventData,
        uint eventDataSize);

    [LibraryImport("hid.dll")]
    internal static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SetupDiGetClassDevs(
        in Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        SafeDeviceInfoSetHandle deviceInfoSet,
        IntPtr deviceInfoData,
        in Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetupDiGetDeviceInterfaceDetail(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        byte* detailData,
        uint detailDataSize,
        out uint requiredSize,
        SpDevInfoData* deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInstanceId(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        [Out] char[] deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetAttributes(
        SafeFileHandle hidDeviceObject,
        ref HiddAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetPreparsedData(
        SafeFileHandle hidDeviceObject,
        out IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetUsageValue(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        out uint usageValue,
        IntPtr preparsedData,
        byte[] report,
        uint reportLength);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetSpecificValueCaps(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        [Out] HidpValueCaps[] valueCaps,
        ref ushort valueCapsLength,
        IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_SetOutputReport(
        SafeFileHandle hidDeviceObject,
        byte[] reportBuffer,
        uint reportBufferLength);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    internal static unsafe partial int CmGetDevNodeProperty(
        uint deviceInstance,
        in DevPropKey propertyKey,
        out uint propertyType,
        byte* propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static partial int CmGetParent(out uint parent, uint deviceInstance, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    internal static partial int CmGetDeviceIdSize(out uint length, uint deviceInstance, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CmGetDeviceId(
        uint deviceInstance,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification", SetLastError = false)]
    internal static extern int CmRegisterNotification(
        ref CmNotifyFilter filter,
        IntPtr context,
        CmNotifyCallback callback,
        out IntPtr notification);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    internal static partial int CmUnregisterNotification(IntPtr notification);
}
