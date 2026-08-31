// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public sealed record HidDeviceSnapshot(
    string DevicePath,
    string InstanceId,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportByteLength,
    ushort OutputReportByteLength,
    ushort FeatureReportByteLength,
    Guid? ContainerId = null,
    IReadOnlyList<string>? LocationPaths = null,
    string? ParentInstanceId = null,
    string? BusReportedDescription = null,
    string? DriverService = null,
    string? DriverProvider = null,
    string? DriverInfPath = null)
{
    public IReadOnlyList<string> EffectiveLocationPaths => LocationPaths ?? [];

    public bool IsJoystick => UsagePage == 0x01 && Usage == 0x04;
}
