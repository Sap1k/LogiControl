// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.DeviceAgent;

public sealed record ModeSwitchDecision(
    bool IsAllowed,
    string Reason,
    WheelIdentity? Identity,
    IReadOnlyList<LogitechCommand> Commands)
{
    public static ModeSwitchDecision Evaluate(
        ushort vendorId,
        ushort productId,
        ushort versionNumber)
    {
        if (!ClassicWheelCatalog.TryIdentify(vendorId, productId, versionNumber, out WheelIdentity? identity) ||
            identity is null)
        {
            return new(false, "Device identity is unknown; output is prohibited.", null, []);
        }

        if (identity.IsNativeMode)
        {
            return new(false, "Device is already in its native USB mode.", identity, []);
        }

        return new(
            true,
            $"Switch {identity.Definition.DisplayName} to PID {identity.Definition.NativeProductId:X4}.",
            identity,
            identity.Definition.NativeModeSwitchSequence);
    }
}
