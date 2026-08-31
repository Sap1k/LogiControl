// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

public enum ForceEffectKind : byte
{
    Constant = 1,
    Ramp = 2,
    Square = 3,
    Sine = 4,
    Triangle = 5,
    SawtoothUp = 6,
    SawtoothDown = 7,
    Spring = 8,
    Damper = 9,
    Friction = 10,
    Inertia = 11,
    Custom = 12,
}

public enum ForceAxis : byte
{
    Steering = 0,
}

[Flags]
public enum EffectUpdateMask : ushort
{
    None = 0,
    Duration = 1 << 0,
    StartDelay = 1 << 1,
    SamplePeriod = 1 << 2,
    Gain = 1 << 3,
    Direction = 1 << 4,
    Envelope = 1 << 5,
    TypeSpecific = 1 << 6,
    Axis = 1 << 7,
    All = Duration | StartDelay | SamplePeriod | Gain | Direction | Envelope | TypeSpecific | Axis,
}

public readonly record struct EffectEnvelope(
    int AttackLevel,
    uint AttackTimeMicroseconds,
    int FadeLevel,
    uint FadeTimeMicroseconds);

public readonly record struct EffectCommon(
    uint DurationMicroseconds,
    uint StartDelayMicroseconds,
    uint SamplePeriodMicroseconds,
    int Gain,
    int Direction,
    EffectEnvelope? Envelope,
    uint AxisIdentifier = 0)
{
    public const uint InfiniteDuration = uint.MaxValue;
}

public abstract record EffectDefinition(EffectCommon Common)
{
    public abstract ForceEffectKind Kind { get; }
}

public sealed record ConstantEffectDefinition(EffectCommon Common, int Magnitude)
    : EffectDefinition(Common)
{
    public override ForceEffectKind Kind => ForceEffectKind.Constant;
}

public sealed record RampEffectDefinition(EffectCommon Common, int Start, int End)
    : EffectDefinition(Common)
{
    public override ForceEffectKind Kind => ForceEffectKind.Ramp;
}

public sealed record PeriodicEffectDefinition(
    EffectCommon Common,
    ForceEffectKind Waveform,
    int Magnitude,
    int Offset,
    uint PhaseHundredthsOfDegree,
    uint PeriodMicroseconds)
    : EffectDefinition(Common)
{
    public override ForceEffectKind Kind => Waveform;
}

public sealed record ConditionEffectDefinition(
    EffectCommon Common,
    ForceEffectKind Condition,
    int Offset,
    int PositiveCoefficient,
    int NegativeCoefficient,
    int PositiveSaturation,
    int NegativeSaturation,
    int DeadBand)
    : EffectDefinition(Common)
{
    public override ForceEffectKind Kind => Condition;
}

public sealed record CustomEffectDefinition : EffectDefinition
{
    private readonly int[] samples;

    public CustomEffectDefinition(EffectCommon common, ReadOnlySpan<int> samples)
        : base(common)
    {
        this.samples = samples.ToArray();
    }

    public override ForceEffectKind Kind => ForceEffectKind.Custom;

    public ReadOnlyMemory<int> Samples => samples;
}

public enum EffectPlaybackState : byte
{
    Downloaded,
    Delayed,
    Playing,
    Paused,
    Stopped,
    Completed,
}

public enum DeviceForceState : byte
{
    Ready,
    Paused,
    ActuatorsOff,
    Faulted,
    Removed,
}

public enum DeviceForceCommand : byte
{
    Pause,
    Continue,
    ActuatorsOn,
    ActuatorsOff,
    StopAll,
    Reset,
    EmergencyStop,
}

public readonly record struct RuntimeSettings(
    int RangeDegrees,
    int MasterGain,
    int PeriodicGain,
    int SpringGain,
    int DamperGain,
    int FrictionGain,
    int BoundaryForce,
    int IdleAutocenter)
{
    public static RuntimeSettings Default => new(900, 10_000, 10_000, 10_000, 10_000, 10_000, 3_000, 0);
}

public static class EffectDefinitionValidator
{
    public const int MaximumEffectsPerSession = 16;
    public const int MaximumCustomSamples = 4096;

    public static bool TryValidate(EffectDefinition? definition, out string error)
    {
        if (definition is null)
        {
            error = "Effect definition is required.";
            return false;
        }

        EffectCommon common = definition.Common;
        if (common.Gain is < 0 or > 10_000 || common.Direction is < -10_000 or > 10_000)
        {
            error = "Gain or direction is outside the DirectInput range.";
            return false;
        }

        if (common.Envelope is { } envelope &&
            (envelope.AttackLevel is < 0 or > 10_000 || envelope.FadeLevel is < 0 or > 10_000))
        {
            error = "Envelope level is outside the DirectInput range.";
            return false;
        }

        bool valid = definition switch
        {
            ConstantEffectDefinition constant => InSignedRange(constant.Magnitude),
            RampEffectDefinition ramp => InSignedRange(ramp.Start) && InSignedRange(ramp.End),
            PeriodicEffectDefinition periodic =>
                periodic.Waveform is ForceEffectKind.Square or ForceEffectKind.Sine or ForceEffectKind.Triangle or
                    ForceEffectKind.SawtoothUp or ForceEffectKind.SawtoothDown &&
                periodic.Magnitude is >= 0 and <= 10_000 && InSignedRange(periodic.Offset) && periodic.PeriodMicroseconds > 0,
            ConditionEffectDefinition condition =>
                condition.Condition is ForceEffectKind.Spring or ForceEffectKind.Damper or ForceEffectKind.Friction or ForceEffectKind.Inertia &&
                InSignedRange(condition.Offset) && InSignedRange(condition.PositiveCoefficient) &&
                InSignedRange(condition.NegativeCoefficient) && condition.PositiveSaturation is >= 0 and <= 10_000 &&
                condition.NegativeSaturation is >= 0 and <= 10_000 && condition.DeadBand is >= 0 and <= 10_000,
            CustomEffectDefinition custom => custom.Samples.Length is >= 1 and <= MaximumCustomSamples &&
                AllSamplesValid(custom.Samples.Span),
            _ => false,
        };

        error = valid ? string.Empty : "Effect-specific parameters are invalid.";
        return valid;
    }

    public static bool TryValidate(RuntimeSettings settings, out string error)
    {
        bool valid = settings.RangeDegrees is >= 40 and <= 900 &&
            InUnsignedRange(settings.MasterGain) && InUnsignedRange(settings.PeriodicGain) &&
            InUnsignedRange(settings.SpringGain) && InUnsignedRange(settings.DamperGain) &&
            InUnsignedRange(settings.FrictionGain) && InUnsignedRange(settings.BoundaryForce) &&
            InUnsignedRange(settings.IdleAutocenter);
        error = valid ? string.Empty : "Runtime setting is outside its supported range.";
        return valid;
    }

    private static bool InSignedRange(int value) => value is >= -10_000 and <= 10_000;

    private static bool InUnsignedRange(int value) => value is >= 0 and <= 10_000;

    private static bool AllSamplesValid(ReadOnlySpan<int> samples)
    {
        foreach (int sample in samples)
        {
            if (!InSignedRange(sample))
            {
                return false;
            }
        }

        return true;
    }
}
