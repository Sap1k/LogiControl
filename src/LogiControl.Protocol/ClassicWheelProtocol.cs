// SPDX-License-Identifier: GPL-3.0-or-later
// Protocol behavior independently expressed from new-lg4ff, commit
// 2092db19f7b40854e0427a1b2e39eda9f8d0c3cd (GPL-2.0-or-later).
// Condition scaling preserves behavior translated from DFGT Control,
// commit 426d7007a1d40e4d2de5c5873959620f9066ec1c (MIT); see THIRD_PARTY_NOTICES.md.

namespace LogiControl.Protocol;

public enum FirmwareSlotOperation : byte
{
    Start = 1,
    Stop = 3,
    Update = 12,
}

/// <summary>
/// Encodes reports for the shared Logitech classic four-slot force-feedback family.
/// Model differences are represented by <see cref="WheelProtocolProfile"/> strategies.
/// </summary>
public sealed class ClassicWheelProtocol
{
    private readonly WheelProtocolProfile profile;
    private readonly SteeringRangeDefinition steeringRange;

    public static ClassicWheelProtocol Default { get; } =
        new(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT));

    public ClassicWheelProtocol(WheelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        profile = definition.ProtocolProfile;
        steeringRange = definition.SteeringRange;
        ValidateProfile();
    }

    public int ReportLength => profile.NativeReportLayout.OutputReportByteLength;

    public bool IsRangeSupported(int degrees) => steeringRange.Supports(degrees);

    public void WriteStopAll(Span<byte> report)
    {
        Prepare(report);
        report[1] = 0xF3;
    }

    public void WriteDisableAutocenter(Span<byte> report)
    {
        Prepare(report);
        report[1] = 0xF5;
    }

    public void WriteEnableAutocenter(Span<byte> report)
    {
        Prepare(report);
        report[1] = 0x14;
    }

    public void WriteSlotStop(Span<byte> report, int slot)
    {
        Prepare(report);
        report[1] = Command(slot, FirmwareSlotOperation.Stop);
    }

    public void WriteConstant(Span<byte> report, int slot, FirmwareSlotOperation operation, int magnitude)
    {
        Prepare(report);
        report[1] = Command(slot, operation);
        report[2] = 0x00;
        report[3 + slot] = TranslateSignedMagnitude(magnitude);
    }

    public IReadOnlyList<byte[]> CreateRangeReports(int degrees)
    {
        if (!IsRangeSupported(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), "The steering range is not supported by this wheel.");
        }

        return profile.SteeringRange switch
        {
            SteeringRangeStrategy.ExtendedCommand81 => [CreateExtendedRangeReport(degrees)],
            SteeringRangeStrategy.DrivingForceProDiscrete => CreateDrivingForceProRangeReports(degrees),
            _ => throw new NotSupportedException($"Unknown steering-range strategy: {profile.SteeringRange}."),
        };
    }

    public void WriteSpring(
        Span<byte> report,
        int slot,
        FirmwareSlotOperation operation,
        int center,
        int deadBand,
        int leftCoefficient,
        int rightCoefficient,
        int leftSaturation,
        int rightSaturation)
    {
        Prepare(report);
        report[1] = Command(slot, operation);
        report[2] = 0x0B;

        ushort d1 = ScaleConditionPosition(center - deadBand / 2);
        ushort d2 = ScaleConditionPosition(center + deadBand / 2);
        uint rawK1 = ScaleSigned16Magnitude(leftCoefficient);
        uint rawK2 = ScaleSigned16Magnitude(rightCoefficient);
        if (rawK1 < 2048)
        {
            d1 = 0;
        }
        else
        {
            rawK1 -= 2048;
        }

        if (rawK2 < 2048)
        {
            d2 = 2047;
        }
        else
        {
            rawK2 -= 2048;
        }

        byte k1 = ScaleRawCoefficient(rawK1, 4);
        byte k2 = ScaleRawCoefficient(rawK2, 4);
        int s1 = leftCoefficient < 0 ? 1 : 0;
        int s2 = rightCoefficient < 0 ? 1 : 0;
        report[3] = (byte)(d1 >> 3);
        report[4] = (byte)(d2 >> 3);
        report[5] = (byte)((k2 << 4) | k1);
        report[6] = (byte)(((d2 & 7) << 5) | ((d1 & 7) << 1) | (s2 << 4) | s1);
        report[7] = ScaleSaturation(Math.Max(leftSaturation, rightSaturation));
    }

    public void WriteDamper(
        Span<byte> report,
        int slot,
        FirmwareSlotOperation operation,
        int center,
        int deadBand,
        int leftCoefficient,
        int rightCoefficient,
        int leftSaturation,
        int rightSaturation)
    {
        _ = center;
        _ = deadBand;
        Prepare(report);
        report[1] = Command(slot, operation);
        report[2] = 0x0C;
        report[3] = ScaleCoefficient(leftCoefficient, 4);
        report[4] = (byte)(leftCoefficient < 0 ? 1 : 0);
        report[5] = ScaleCoefficient(rightCoefficient, 4);
        report[6] = (byte)(rightCoefficient < 0 ? 1 : 0);
        report[7] = ScaleSaturation(Math.Max(leftSaturation, rightSaturation));
    }

    public void WriteFriction(
        Span<byte> report,
        int slot,
        FirmwareSlotOperation operation,
        int center,
        int deadBand,
        int leftCoefficient,
        int rightCoefficient,
        int leftSaturation,
        int rightSaturation)
    {
        _ = center;
        _ = deadBand;
        Prepare(report);
        report[1] = Command(slot, operation);
        report[2] = 0x0E;
        report[3] = ScaleCoefficient(leftCoefficient, 8);
        report[4] = ScaleCoefficient(rightCoefficient, 8);
        report[5] = ScaleSaturation(Math.Max(leftSaturation, rightSaturation));
        report[6] = (byte)((rightCoefficient < 0 ? 0x10 : 0) | (leftCoefficient < 0 ? 0x01 : 0));
    }

    public void WriteCondition(
        Span<byte> report,
        int slot,
        FirmwareSlotOperation operation,
        ConditionEffectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        switch (definition.Condition)
        {
            case ForceEffectKind.Spring:
                WriteSpring(report, slot, operation, definition.Offset, definition.DeadBand,
                    definition.NegativeCoefficient, definition.PositiveCoefficient,
                    definition.NegativeSaturation, definition.PositiveSaturation);
                break;
            case ForceEffectKind.Friction when profile.SupportsNativeFriction:
                WriteFriction(report, slot, operation, definition.Offset, definition.DeadBand,
                    definition.NegativeCoefficient, definition.PositiveCoefficient,
                    definition.NegativeSaturation, definition.PositiveSaturation);
                break;
            case ForceEffectKind.Damper:
            case ForceEffectKind.Inertia:
            case ForceEffectKind.Friction:
                WriteDamper(report, slot, operation, definition.Offset, definition.DeadBand,
                    definition.NegativeCoefficient, definition.PositiveCoefficient,
                    definition.NegativeSaturation, definition.PositiveSaturation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(definition), "The effect is not a condition effect.");
        }
    }

    public void WriteAutocenterParameters(Span<byte> report, int magnitude)
    {
        if (profile.Autocenter != AutocenterStrategy.Classic)
        {
            throw new NotSupportedException($"Unknown autocenter strategy: {profile.Autocenter}.");
        }

        Prepare(report);
        uint scaled = (uint)Math.Clamp(magnitude, 0, 10_000) * 0xFFFFu / 10_000u;
        const uint knee = 0xAAAA;
        uint expandedA;
        uint expandedB;
        if (scaled <= knee)
        {
            expandedA = 0x0Cu * scaled;
            expandedB = 0x80u * scaled;
        }
        else
        {
            expandedA = 0x0Cu * knee + 0x06u * (scaled - knee);
            expandedB = 0x80u * knee + 0xFFu * (scaled - knee);
        }

        expandedA >>= 1;
        report[1] = 0xFE;
        report[2] = 0x0D;
        report[3] = ClampByte(expandedA / knee);
        report[4] = report[3];
        report[5] = ClampByte(expandedB / knee);
    }

    private byte[] CreateExtendedRangeReport(int degrees)
    {
        var report = new byte[ReportLength];
        Prepare(report);
        report[1] = 0xF8;
        report[2] = 0x81;
        report[3] = (byte)degrees;
        report[4] = (byte)(degrees >> 8);
        return report;
    }

    private IReadOnlyList<byte[]> CreateDrivingForceProRangeReports(int degrees)
    {
        var coarse = new byte[ReportLength];
        Prepare(coarse);
        coarse[1] = 0xF8;
        coarse[2] = degrees switch
        {
            200 => 0x02,
            900 => 0x03,
            _ => throw new ArgumentOutOfRangeException(
                nameof(degrees), "Driving Force Pro supports only 200 or 900 degrees."),
        };

        var fine = new byte[ReportLength];
        Prepare(fine);
        fine[1] = 0x81;
        fine[2] = 0x0B;
        return [coarse, fine];
    }

    private void Prepare(Span<byte> report)
    {
        if (report.Length < ReportLength)
        {
            throw new ArgumentException($"The force-feedback report must be at least {ReportLength} bytes.", nameof(report));
        }

        report[..ReportLength].Clear();
        report[0] = profile.NativeReportLayout.ReportId;
    }

    private void ValidateProfile()
    {
        profile.NativeReportLayout.Validate();
        if (profile.ForceFeedbackFamily != ForceFeedbackProtocolFamily.LogitechClassicFourSlot ||
            profile.NativeReportLayout.CommandPayloadLength != LogitechCommand.Length)
        {
            throw new NotSupportedException("Only the Logitech classic four-slot protocol is currently supported.");
        }
    }

    private static byte Command(int slot, FirmwareSlotOperation operation)
    {
        if (slot is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return (byte)((0x10 << slot) | (byte)operation);
    }

    private static byte TranslateSignedMagnitude(int magnitude)
    {
        int clamped = Math.Clamp(magnitude, -10_000, 10_000);
        int scaled = (int)Math.Round(clamped / 10_000.0 * 127.0, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(0x80 + scaled, 0x01, 0xFF);
    }

    private static ushort ScaleConditionPosition(int value)
    {
        long bounded = Math.Clamp((long)value, -10_000, 10_000);
        return (ushort)(((bounded + 10_000) * 65_535 / 20_000) >> 5);
    }

    private static uint ScaleSigned16Magnitude(int coefficient)
    {
        long bounded = Math.Clamp((long)coefficient, -10_000, 10_000);
        long magnitude = bounded < 0 ? -bounded : bounded;
        return (uint)(magnitude * 32_767 / 10_000);
    }

    private static byte ScaleRawCoefficient(uint rawMagnitude, int bits)
    {
        uint doubled = Math.Min(rawMagnitude * 2, 65_535u);
        return (byte)(doubled >> (16 - bits));
    }

    private static byte ScaleCoefficient(int value, int bits) =>
        ScaleRawCoefficient(ScaleSigned16Magnitude(value), bits);

    private static byte ScaleSaturation(int saturation)
    {
        long bounded = Math.Clamp((long)saturation, 0, 10_000);
        return (byte)((bounded * 65_535 / 10_000) >> 8);
    }

    private static byte ClampByte(uint value) => (byte)Math.Min(value, byte.MaxValue);
}
