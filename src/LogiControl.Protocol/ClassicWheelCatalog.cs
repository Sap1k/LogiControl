// SPDX-License-Identifier: GPL-3.0-or-later
// Protocol behavior independently expressed from new-lg4ff, commit
// 2092db19f7b40854e0427a1b2e39eda9f8d0c3cd (GPL-2.0-or-later).

namespace LogiControl.Protocol;

public static class ClassicWheelCatalog
{
    public const ushort LogitechVendorId = 0x046D;
    public const ushort CompatibilityProductId = 0xC294;
    public const int MaximumCandidates = 8;

    private static readonly HidReportLayout ClassicLayout = HidReportLayout.ClassicUnnumbered;
    private static readonly SteeringRangeDefinition ContinuousRange = new(40, 900);
    private static readonly SteeringRangeDefinition DrivingForceProRange = new(200, 900, [200, 900]);
    private static readonly WheelCapabilities CommonCapabilities =
        WheelCapabilities.NativeModeSwitch |
        WheelCapabilities.SteeringRange |
        WheelCapabilities.Autocenter |
        WheelCapabilities.ForceFeedback |
        WheelCapabilities.NativeFriction;

    // Ordering is significant: G27 precedes the broader G25 family, and DFP is last.
    private static readonly WheelDefinition[] Definitions =
    [
        CreateDefinition(
            WheelModel.DrivingForceGT,
            "Driving Force GT",
            new WheelRevisionMatcher(0xFF00, 0x1300),
            [
                Presentation(0xC294, UsbPresentationMode.DrivingForceCompatibility),
                Presentation(0xC298, UsbPresentationMode.DrivingForcePro),
                Presentation(0xC29A, UsbPresentationMode.DrivingForceGT, preferred: true),
            ],
            [
                Step(false, 0xF8, 0x0A, 0, 0, 0, 0, 0),
                Step(true, 0xF8, 0x09, 0x03, 0x01, 0, 0, 0),
            ],
            SteeringRangeStrategy.ExtendedCommand81,
            ContinuousRange),
        CreateDefinition(
            WheelModel.G27,
            "G27 Racing Wheel",
            new WheelRevisionMatcher(0xFFF0, 0x1230),
            [
                Presentation(0xC294, UsbPresentationMode.DrivingForceCompatibility),
                Presentation(0xC298, UsbPresentationMode.DrivingForcePro),
                Presentation(0xC299, UsbPresentationMode.G25),
                Presentation(0xC29B, UsbPresentationMode.G27, preferred: true),
            ],
            [
                Step(false, 0xF8, 0x0A, 0, 0, 0, 0, 0),
                Step(true, 0xF8, 0x09, 0x04, 0x01, 0, 0, 0),
            ],
            SteeringRangeStrategy.ExtendedCommand81,
            ContinuousRange),
        CreateDefinition(
            WheelModel.G25,
            "G25 Racing Wheel",
            new WheelRevisionMatcher(0xFF00, 0x1200),
            [
                Presentation(0xC294, UsbPresentationMode.DrivingForceCompatibility),
                Presentation(0xC298, UsbPresentationMode.DrivingForcePro),
                Presentation(0xC299, UsbPresentationMode.G25, preferred: true),
            ],
            [Step(true, 0xF8, 0x10, 0, 0, 0, 0, 0)],
            SteeringRangeStrategy.ExtendedCommand81,
            ContinuousRange),
        CreateDefinition(
            WheelModel.DrivingForcePro,
            "Driving Force Pro",
            new WheelRevisionMatcher(0xF000, 0x1000),
            [
                Presentation(0xC294, UsbPresentationMode.DrivingForceCompatibility),
                Presentation(0xC298, UsbPresentationMode.DrivingForcePro, preferred: true),
            ],
            [Step(true, 0xF8, 0x01, 0, 0, 0, 0, 0)],
            SteeringRangeStrategy.DrivingForceProDiscrete,
            DrivingForceProRange),
    ];

    public static IReadOnlyList<WheelDefinition> All => Definitions;

    public static WheelDefinition GetDefinition(WheelModel model) =>
        Definitions.FirstOrDefault(definition => definition.Model == model) ??
        throw new ArgumentOutOfRangeException(nameof(model));

    public static bool TryIdentify(
        ushort vendorId,
        ushort presentedProductId,
        ushort versionNumber,
        out WheelIdentity? identity)
    {
        identity = null;
        if (vendorId != LogitechVendorId)
        {
            return false;
        }

        foreach (WheelDefinition definition in Definitions)
        {
            if (definition.MatchesRevision(versionNumber) &&
                definition.TryGetPresentation(presentedProductId, out WheelPresentationDefinition? presentation) &&
                presentation is not null)
            {
                identity = new WheelIdentity(definition, presentation, vendorId, versionNumber);
                return true;
            }
        }

        return false;
    }

    private static WheelDefinition CreateDefinition(
        WheelModel model,
        string name,
        WheelRevisionMatcher matcher,
        WheelPresentationDefinition[] presentations,
        ModeSwitchStep[] switchSteps,
        SteeringRangeStrategy rangeStrategy,
        SteeringRangeDefinition steeringRange)
    {
        var profile = new WheelProtocolProfile(
            ForceFeedbackProtocolFamily.LogitechClassicFourSlot,
            rangeStrategy,
            AutocenterStrategy.Classic,
            SupportsNativeFriction: true,
            ClassicLayout);
        return new WheelDefinition(
            model,
            name,
            [matcher],
            presentations,
            new ModeSwitchPlan(switchSteps),
            steeringRange,
            CommonCapabilities,
            profile);
    }

    private static WheelPresentationDefinition Presentation(
        ushort productId,
        UsbPresentationMode mode,
        bool preferred = false) =>
        new(productId, mode, ClassicLayout, preferred);

    private static ModeSwitchStep Step(bool detachExpected, params byte[] command) =>
        new(new LogitechCommand(command), ClassicLayout.ReportId, detachExpected);
}
