// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace LogiControl.Protocol;

public static class EffectDefinitionCodec
{
    private const byte HasEnvelope = 1;
    private const int CommonLength = 28;
    private const int EnvelopeLength = 16;

    public static int GetEncodedLength(EffectDefinition definition) =>
        CommonLength + (definition.Common.Envelope.HasValue ? EnvelopeLength : 0) + definition switch
        {
            ConstantEffectDefinition => 4,
            RampEffectDefinition => 8,
            PeriodicEffectDefinition => 16,
            ConditionEffectDefinition => 28,
            CustomEffectDefinition custom => checked(4 + custom.Samples.Length * 4),
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        };

    public static bool TryWrite(Span<byte> destination, EffectDefinition definition, out int written)
    {
        written = 0;
        if (!EffectDefinitionValidator.TryValidate(definition, out _))
        {
            return false;
        }

        int length = GetEncodedLength(definition);
        if (destination.Length < length || length > IpcFrameCodec.MaximumPayloadLength)
        {
            return false;
        }

        destination[..length].Clear();
        destination[0] = (byte)definition.Kind;
        destination[1] = definition.Common.Envelope.HasValue ? HasEnvelope : (byte)0;
        WriteUInt32(destination, 4, definition.Common.DurationMicroseconds);
        WriteUInt32(destination, 8, definition.Common.StartDelayMicroseconds);
        WriteUInt32(destination, 12, definition.Common.SamplePeriodMicroseconds);
        WriteInt32(destination, 16, definition.Common.Gain);
        WriteInt32(destination, 20, definition.Common.Direction);
        WriteUInt32(destination, 24, definition.Common.AxisIdentifier);
        int offset = CommonLength;
        if (definition.Common.Envelope is { } envelope)
        {
            WriteInt32(destination, offset, envelope.AttackLevel);
            WriteUInt32(destination, offset + 4, envelope.AttackTimeMicroseconds);
            WriteInt32(destination, offset + 8, envelope.FadeLevel);
            WriteUInt32(destination, offset + 12, envelope.FadeTimeMicroseconds);
            offset += EnvelopeLength;
        }

        switch (definition)
        {
            case ConstantEffectDefinition constant:
                WriteInt32(destination, offset, constant.Magnitude);
                break;
            case RampEffectDefinition ramp:
                WriteInt32(destination, offset, ramp.Start);
                WriteInt32(destination, offset + 4, ramp.End);
                break;
            case PeriodicEffectDefinition periodic:
                WriteInt32(destination, offset, periodic.Magnitude);
                WriteInt32(destination, offset + 4, periodic.Offset);
                WriteUInt32(destination, offset + 8, periodic.PhaseHundredthsOfDegree);
                WriteUInt32(destination, offset + 12, periodic.PeriodMicroseconds);
                break;
            case ConditionEffectDefinition condition:
                WriteInt32(destination, offset, condition.Offset);
                WriteInt32(destination, offset + 4, condition.PositiveCoefficient);
                WriteInt32(destination, offset + 8, condition.NegativeCoefficient);
                WriteInt32(destination, offset + 12, condition.PositiveSaturation);
                WriteInt32(destination, offset + 16, condition.NegativeSaturation);
                WriteInt32(destination, offset + 20, condition.DeadBand);
                WriteInt32(destination, offset + 24, 0);
                break;
            case CustomEffectDefinition custom:
                WriteUInt32(destination, offset, (uint)custom.Samples.Length);
                offset += 4;
                foreach (int sample in custom.Samples.Span)
                {
                    WriteInt32(destination, offset, sample);
                    offset += 4;
                }

                break;
        }

        written = length;
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out EffectDefinition? definition, bool validate = true)
    {
        definition = null;
        if (source.Length < CommonLength || (source[1] & ~HasEnvelope) != 0 || source[2] != 0 || source[3] != 0)
        {
            return false;
        }

        ForceEffectKind kind = (ForceEffectKind)source[0];
        int offset = CommonLength;
        EffectEnvelope? envelope = null;
        if ((source[1] & HasEnvelope) != 0)
        {
            if (source.Length < offset + EnvelopeLength)
            {
                return false;
            }

            envelope = new EffectEnvelope(
                ReadInt32(source, offset),
                ReadUInt32(source, offset + 4),
                ReadInt32(source, offset + 8),
                ReadUInt32(source, offset + 12));
            offset += EnvelopeLength;
        }

        var common = new EffectCommon(
            ReadUInt32(source, 4),
            ReadUInt32(source, 8),
            ReadUInt32(source, 12),
            ReadInt32(source, 16),
            ReadInt32(source, 20),
            envelope,
            ReadUInt32(source, 24));

        int expected;
        switch (kind)
        {
            case ForceEffectKind.Constant:
                expected = offset + 4;
                if (source.Length != expected) return false;
                definition = new ConstantEffectDefinition(common, ReadInt32(source, offset));
                break;
            case ForceEffectKind.Ramp:
                expected = offset + 8;
                if (source.Length != expected) return false;
                definition = new RampEffectDefinition(common, ReadInt32(source, offset), ReadInt32(source, offset + 4));
                break;
            case ForceEffectKind.Square:
            case ForceEffectKind.Sine:
            case ForceEffectKind.Triangle:
            case ForceEffectKind.SawtoothUp:
            case ForceEffectKind.SawtoothDown:
                expected = offset + 16;
                if (source.Length != expected) return false;
                definition = new PeriodicEffectDefinition(common, kind, ReadInt32(source, offset), ReadInt32(source, offset + 4),
                    ReadUInt32(source, offset + 8), ReadUInt32(source, offset + 12));
                break;
            case ForceEffectKind.Spring:
            case ForceEffectKind.Damper:
            case ForceEffectKind.Friction:
            case ForceEffectKind.Inertia:
                expected = offset + 28;
                if (source.Length != expected || ReadInt32(source, offset + 24) != 0) return false;
                definition = new ConditionEffectDefinition(common, kind, ReadInt32(source, offset), ReadInt32(source, offset + 4),
                    ReadInt32(source, offset + 8), ReadInt32(source, offset + 12), ReadInt32(source, offset + 16),
                    ReadInt32(source, offset + 20));
                break;
            case ForceEffectKind.Custom:
                if (source.Length < offset + 4) return false;
                uint count = ReadUInt32(source, offset);
                if (count is < 1 or > EffectDefinitionValidator.MaximumCustomSamples ||
                    source.Length != offset + 4 + count * 4L)
                {
                    return false;
                }

                var samples = new int[count];
                offset += 4;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = ReadInt32(source, offset + i * 4);
                }

                definition = new CustomEffectDefinition(common, samples);
                break;
            default:
                return false;
        }

        return !validate || EffectDefinitionValidator.TryValidate(definition, out _);
    }

    private static void WriteUInt32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], value);

    private static void WriteInt32(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
}
