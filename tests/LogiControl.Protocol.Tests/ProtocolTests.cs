// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Protocol.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void C294DfgtIdentityIsRevisionGated()
    {
        Assert.True(ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity));
        WheelIdentity actual = Assert.IsType<WheelIdentity>(identity);
        Assert.Equal(WheelModel.DrivingForceGT, actual.Definition.Model);
        Assert.Equal((ushort)0xC294, actual.PresentedProductId);
        Assert.False(actual.IsNativeMode);
    }

    [Fact]
    public void DfgtNativeSwitchMatchesGoldenVectors()
    {
        ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity);
        IReadOnlyList<LogitechCommand> commands = identity!.Definition.NativeModeSwitchSequence;

        Assert.Equal(2, commands.Count);
        Assert.Equal(new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 }, commands[0].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xF8, 0x09, 0x03, 0x01, 0, 0, 0 }, commands[1].Bytes.ToArray());
        Assert.Equal((ushort)0xC29A, identity.Definition.NativeProductId);
    }

    [Theory]
    [InlineData(0x1234, WheelModel.G27)]
    [InlineData(0x1224, WheelModel.G25)]
    [InlineData(0x1210, WheelModel.DrivingForcePro)]
    public void OverlappingClassicRevisionsIdentifyInSpecificOrder(int revision, WheelModel expected)
    {
        Assert.True(ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, (ushort)revision, out WheelIdentity? identity));
        Assert.Equal(expected, identity!.Definition.Model);
    }

    [Fact]
    public void UnknownC294RevisionIsRejected() =>
        Assert.False(ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x2000, out _));

    [Fact]
    public void OtherVendorIsRejected() =>
        Assert.False(ClassicWheelCatalog.TryIdentify(0x1234, 0xC294, 0x1301, out _));

    [Fact]
    public void CommandStorageIsImmutable()
    {
        var source = new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 };
        var command = new LogitechCommand(source);
        source[0] = 0;
        byte[] copy = command.ToArray();
        copy[1] = 0;

        Assert.Equal(0xF8, command.Bytes.Span[0]);
        Assert.Equal(0x0A, command.Bytes.Span[1]);
    }

    [Fact]
    public void SemanticIpcHeaderMatchesGoldenVector()
    {
        Span<byte> bytes = stackalloc byte[IpcFrameCodec.HeaderLength];
        var header = new IpcFrameHeader(1, 0, IpcMessageType.StartEffect, IpcFrameFlags.None, 4, 0x0102030405060708, 9);
        Assert.True(IpcFrameCodec.TryWriteHeader(bytes, header));
        Assert.Equal(new byte[]
        {
            0x4C, 0x43, 0x46, 0x46, 1, 0, 0, 0, 12, 0, 0, 0, 4, 0, 0, 0,
            8, 7, 6, 5, 4, 3, 2, 1, 9, 0, 0, 0, 0, 0, 0, 0,
        }, bytes.ToArray());
        Assert.True(IpcFrameCodec.TryReadHeader(bytes, out IpcFrameHeader decoded));
        Assert.Equal(header, decoded);
    }

    [Fact]
    public void SemanticIpcRejectsOversizedFrame()
    {
        Span<byte> bytes = stackalloc byte[IpcFrameCodec.HeaderLength];
        var header = new IpcFrameHeader(1, 0, IpcMessageType.Hello, IpcFrameFlags.None,
            IpcFrameCodec.MaximumPayloadLength + 1u, 1, 0);
        Assert.False(IpcFrameCodec.TryWriteHeader(bytes, header));
    }

    [Fact]
    public void DfgtFirmwareSlotsMatchGoldenVectors()
    {
        Span<byte> report = stackalloc byte[DfgtForceFeedbackReports.ReportLength];
        DfgtForceFeedbackReports.WriteConstant(report, 0, FirmwareSlotOperation.Start, 0);
        Assert.Equal(new byte[] { 0, 0x11, 0x08, 0x80, 0x80, 0, 0, 0 }, report.ToArray());
        DfgtForceFeedbackReports.WriteConstant(report, 0, FirmwareSlotOperation.Start, -10_000);
        Assert.Equal(new byte[] { 0, 0x11, 0x08, 0x01, 0x80, 0, 0, 0 }, report.ToArray());
        DfgtForceFeedbackReports.WriteConstant(report, 0, FirmwareSlotOperation.Start, 10_000);
        Assert.Equal(new byte[] { 0, 0x11, 0x08, 0xFF, 0x80, 0, 0, 0 }, report.ToArray());

        byte[] commands = { 0x13, 0x23, 0x43, 0x83 };
        for (int slot = 0; slot < 4; slot++)
        {
            DfgtForceFeedbackReports.WriteSlotStop(report, slot);
            Assert.Equal(commands[slot], report[1]);
        }

        DfgtForceFeedbackReports.WriteSpring(report, 1, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x21, 0x0B, 0x7F, 0x7F, 0, 0xFF, 0xFF }, report.ToArray());
        DfgtForceFeedbackReports.WriteSpring(report, 1, FirmwareSlotOperation.Start,
            0, 0, -1_500, -1_500, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x21, 0x0B, 0x7F, 0x7F, 0x11, 0xFF, 0xFF }, report.ToArray());
        DfgtForceFeedbackReports.WriteDamper(report, 2, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x41, 0x0C, 0x01, 0x01, 0x01, 0x01, 0xFF }, report.ToArray());
        DfgtForceFeedbackReports.WriteFriction(report, 3, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x81, 0x0E, 0x11, 0x11, 0xFF, 0x11, 0 }, report.ToArray());
        DfgtForceFeedbackReports.WriteDamper(report, 2, FirmwareSlotOperation.Start,
            int.MinValue, int.MaxValue, int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        Assert.Equal(new byte[] { 0, 0x41, 0x0C, 0x0F, 0x01, 0x0F, 0x01, 0xFF }, report.ToArray());
    }

    [Fact]
    public void CustomSamplesAreBoundedAndImmutable()
    {
        int[] source = { -10_000, 0, 10_000 };
        var effect = new CustomEffectDefinition(new EffectCommon(3_000, 0, 1_000, 10_000, 10_000, null), source);
        source[0] = 123;

        Assert.Equal(-10_000, effect.Samples.Span[0]);
        Assert.True(EffectDefinitionValidator.TryValidate(effect, out _));
    }

    [Fact]
    public void SemanticEffectPayloadRoundTripsWithoutNativeLayouts()
    {
        var original = new PeriodicEffectDefinition(
            new EffectCommon(2_000_000, 10_000, 2_000, 7_500, -10_000, new EffectEnvelope(1_000, 20_000, 2_000, 30_000)),
            ForceEffectKind.Triangle,
            8_000,
            -500,
            9_000,
            100_000);
        byte[] payload = new byte[EffectDefinitionCodec.GetEncodedLength(original)];

        Assert.True(EffectDefinitionCodec.TryWrite(payload, original, out int written));
        Assert.Equal(payload.Length, written);
        Assert.True(EffectDefinitionCodec.TryRead(payload, out EffectDefinition? decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void SemanticEffectPayloadRejectsTruncationAndOversizedCustomCount()
    {
        var original = new CustomEffectDefinition(
            new EffectCommon(3_000, 0, 1_000, 10_000, 10_000, null),
            new[] { 1, 2, 3 });
        byte[] payload = new byte[EffectDefinitionCodec.GetEncodedLength(original)];
        EffectDefinitionCodec.TryWrite(payload, original, out _);

        Assert.False(EffectDefinitionCodec.TryRead(payload.AsSpan(0, payload.Length - 1), out _));
        payload[28] = 0x01;
        payload[29] = 0x10;
        Assert.False(EffectDefinitionCodec.TryRead(payload, out _));
    }
}
