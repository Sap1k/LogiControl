// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker;

public readonly record struct WheelDeviceId(ulong Value)
{
    public static WheelDeviceId Automatic => default;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record BrokerWheelCandidate(
    WheelDeviceId DeviceId,
    WheelModel Model,
    string DisplayName,
    ushort VersionNumber,
    ushort PresentedProductId,
    string DevicePath,
    BrokerDeviceLifecycleState LifecycleState,
    bool IsSelected,
    bool IsReady);
