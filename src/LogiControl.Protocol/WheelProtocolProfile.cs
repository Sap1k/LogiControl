// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

public enum UsbPresentationMode
{
    DrivingForceCompatibility,
    DrivingForcePro,
    G25,
    DrivingForceGT,
    G27,
}

public enum ForceFeedbackProtocolFamily
{
    LogitechClassicFourSlot,
}

public enum SteeringRangeStrategy
{
    ExtendedCommand81,
    DrivingForceProDiscrete,
}

public enum AutocenterStrategy
{
    Classic,
}

public readonly record struct HidReportLayout(
    int OutputReportByteLength,
    byte ReportId,
    int CommandPayloadLength)
{
    public static HidReportLayout ClassicUnnumbered { get; } = new(8, 0, LogitechCommand.Length);

    public void Validate()
    {
        if (OutputReportByteLength <= 0 || CommandPayloadLength <= 0 ||
            CommandPayloadLength + 1 > OutputReportByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputReportByteLength), "The HID report layout is invalid.");
        }
    }
}

public sealed record WheelPresentationDefinition(
    ushort ProductId,
    UsbPresentationMode Mode,
    HidReportLayout ReportLayout,
    bool IsPreferred)
{
    public WheelPresentationDefinition Validate()
    {
        ReportLayout.Validate();
        return this;
    }
}

public sealed record ModeSwitchStep(LogitechCommand Command, byte ReportId, bool DetachExpected);

public sealed class ModeSwitchPlan
{
    private readonly ModeSwitchStep[] steps;

    public ModeSwitchPlan(IEnumerable<ModeSwitchStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps.ToArray();
        if (this.steps.Length == 0)
        {
            throw new ArgumentException("At least one mode-switch step is required.", nameof(steps));
        }

        if (!this.steps[^1].DetachExpected || this.steps[..^1].Any(static step => step.DetachExpected))
        {
            throw new ArgumentException("Only the final mode-switch step may expect detach.", nameof(steps));
        }
    }

    public IReadOnlyList<ModeSwitchStep> Steps => steps;
}

public sealed record WheelProtocolProfile(
    ForceFeedbackProtocolFamily ForceFeedbackFamily,
    SteeringRangeStrategy SteeringRange,
    AutocenterStrategy Autocenter,
    bool SupportsNativeFriction,
    HidReportLayout NativeReportLayout);
