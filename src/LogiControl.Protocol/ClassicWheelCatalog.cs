// SPDX-License-Identifier: GPL-3.0-or-later
// Protocol behavior independently expressed from new-lg4ff, commit
// 2092db19f7b40854e0427a1b2e39eda9f8d0c3cd (GPL-2.0-or-later).

namespace LogiControl.Protocol;

public static class ClassicWheelCatalog
{
    public const ushort LogitechVendorId = 0x046D;
    public const ushort CompatibilityProductId = 0xC294;

    private static readonly WheelCapabilities CommonCapabilities =
        WheelCapabilities.NativeModeSwitch |
        WheelCapabilities.SteeringRange |
        WheelCapabilities.Autocenter |
        WheelCapabilities.ForceFeedback;

    // Ordering is significant because the DFP match is deliberately broad.
    private static readonly WheelDefinition[] Definitions =
    [
        new(
            WheelModel.DrivingForceGT,
            "Driving Force GT",
            new WheelRevisionMatcher(0xFF00, 0x1300),
            0xC29A,
            [0xC294, 0xC298, 0xC29A],
            [
                new LogitechCommand(0xF8, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00),
                new LogitechCommand(0xF8, 0x09, 0x03, 0x01, 0x00, 0x00, 0x00),
            ],
            CommonCapabilities),
        new(
            WheelModel.G27,
            "G27 Racing Wheel",
            new WheelRevisionMatcher(0xFFF0, 0x1230),
            0xC29B,
            [0xC294, 0xC298, 0xC299, 0xC29B],
            [
                new LogitechCommand(0xF8, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00),
                new LogitechCommand(0xF8, 0x09, 0x04, 0x01, 0x00, 0x00, 0x00),
            ],
            CommonCapabilities),
        new(
            WheelModel.G25,
            "G25 Racing Wheel",
            new WheelRevisionMatcher(0xFFF0, 0x1220),
            0xC299,
            [0xC294, 0xC298, 0xC299],
            [new LogitechCommand(0xF8, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00)],
            CommonCapabilities),
        new(
            WheelModel.DrivingForcePro,
            "Driving Force Pro",
            new WheelRevisionMatcher(0xF000, 0x1000),
            0xC298,
            [0xC294, 0xC298],
            [new LogitechCommand(0xF8, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00)],
            CommonCapabilities),
    ];

    public static IReadOnlyList<WheelDefinition> All => Definitions;

    public static bool TryIdentify(
        ushort vendorId,
        ushort presentedProductId,
        ushort versionNumber,
        out WheelIdentity? identity)
    {
        if (vendorId != LogitechVendorId)
        {
            identity = null;
            return false;
        }

        WheelDefinition? match = Definitions.FirstOrDefault(definition =>
            definition.CanPresentAs(presentedProductId) &&
            definition.RevisionMatcher.Matches(versionNumber));

        identity = match is null
            ? null
            : new WheelIdentity(match, vendorId, presentedProductId, versionNumber);

        return identity is not null;
    }
}
