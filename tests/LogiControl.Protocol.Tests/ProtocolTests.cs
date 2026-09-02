// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Protocol.Tests;

public sealed class ProtocolTests
{
    public static TheoryData<WheelModel, ushort, ushort, byte[]> PreferredModeCases => new()
    {
        {
            WheelModel.DrivingForceGT, 0x1301, 0xC29A,
            [0xF8, 0x0A, 0, 0, 0, 0, 0, 0xF8, 0x09, 0x03, 0x01, 0, 0, 0]
        },
        {
            WheelModel.G27, 0x1234, 0xC29B,
            [0xF8, 0x0A, 0, 0, 0, 0, 0, 0xF8, 0x09, 0x04, 0x01, 0, 0, 0]
        },
        { WheelModel.G25, 0x1201, 0xC299, [0xF8, 0x10, 0, 0, 0, 0, 0] },
        { WheelModel.DrivingForcePro, 0x1001, 0xC298, [0xF8, 0x01, 0, 0, 0, 0, 0] },
    };

    [Fact]
    public void C294DfgtIdentityIsRevisionGated()
    {
        Assert.True(ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity));
        WheelIdentity actual = Assert.IsType<WheelIdentity>(identity);
        Assert.Equal(WheelModel.DrivingForceGT, actual.Definition.Model);
        Assert.Equal((ushort)0xC294, actual.PresentedProductId);
        Assert.False(actual.IsPreferredMode);
    }

    [Fact]
    public void DfgtNativeSwitchMatchesGoldenVectors()
    {
        ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity);
        IReadOnlyList<ModeSwitchStep> steps = identity!.Definition.PreferredModeSwitch.Steps;

        Assert.Equal(2, steps.Count);
        Assert.Equal(new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 }, steps[0].Command.Bytes.ToArray());
        Assert.Equal(new byte[] { 0xF8, 0x09, 0x03, 0x01, 0, 0, 0 }, steps[1].Command.Bytes.ToArray());
        Assert.Equal((ushort)0xC29A, identity.Definition.PreferredProductId);
    }

    [Theory]
    [InlineData(0x1234, WheelModel.G27)]
    [InlineData(0x1224, WheelModel.G25)]
    [InlineData(0x1210, WheelModel.G25)]
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

    [Theory]
    [MemberData(nameof(PreferredModeCases))]
    public void ClassicDefinitionsDescribePreferredModeWithoutModelBranches(
        WheelModel model,
        ushort revision,
        ushort preferredProductId,
        byte[] flattenedCommands)
    {
        WheelDefinition definition = ClassicWheelCatalog.GetDefinition(model);

        Assert.True(definition.MatchesRevision(revision));
        Assert.Equal(preferredProductId, definition.PreferredProductId);
        Assert.Equal(preferredProductId, definition.PreferredPresentation.ProductId);
        Assert.True(definition.PreferredPresentation.IsPreferred);
        Assert.Equal(model == WheelModel.DrivingForcePro ? 200 : 40, definition.MinimumRangeDegrees);
        Assert.Equal(900, definition.MaximumRangeDegrees);
        Assert.True(definition.Capabilities.HasFlag(WheelCapabilities.NativeFriction));
        Assert.All(definition.Presentations, static presentation =>
        {
            Assert.Equal(8, presentation.ReportLayout.OutputReportByteLength);
            Assert.Equal(0, presentation.ReportLayout.ReportId);
            Assert.Equal(7, presentation.ReportLayout.CommandPayloadLength);
        });

        Assert.Equal(flattenedCommands,
            definition.PreferredModeSwitch.Steps.SelectMany(static step => step.Command.Bytes.ToArray()).ToArray());
        Assert.True(definition.PreferredModeSwitch.Steps[^1].DetachExpected);
        Assert.All(definition.PreferredModeSwitch.Steps.Take(definition.PreferredModeSwitch.Steps.Count - 1),
            static step => Assert.False(step.DetachExpected));
    }

    [Theory]
    [InlineData(0xC294, 0x1234, WheelModel.G27, false)]
    [InlineData(0xC298, 0x1234, WheelModel.G27, false)]
    [InlineData(0xC299, 0x1234, WheelModel.G27, false)]
    [InlineData(0xC29B, 0x1234, WheelModel.G27, true)]
    [InlineData(0xC294, 0x1201, WheelModel.G25, false)]
    [InlineData(0xC298, 0x1201, WheelModel.G25, false)]
    [InlineData(0xC299, 0x1201, WheelModel.G25, true)]
    [InlineData(0xC294, 0x1001, WheelModel.DrivingForcePro, false)]
    [InlineData(0xC298, 0x1001, WheelModel.DrivingForcePro, true)]
    public void LowerPresentationsResolveToPhysicalModel(
        int productId,
        int revision,
        WheelModel expectedModel,
        bool preferred)
    {
        Assert.True(ClassicWheelCatalog.TryIdentify(
            ClassicWheelCatalog.LogitechVendorId, (ushort)productId, (ushort)revision, out WheelIdentity? identity));
        Assert.Equal(expectedModel, identity!.Definition.Model);
        Assert.Equal(preferred, identity.IsPreferredMode);
        Assert.Equal((ushort)productId, identity.Presentation.ProductId);
    }

    [Fact]
    public void ClassicSharedRangeAndDfpSupportedRangesMatchGoldenVectors()
    {
        var shared = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(WheelModel.G27));
        var dfp = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForcePro));

        Assert.Equal(new byte[] { 0, 0xF8, 0x81, 0x1C, 0x02, 0, 0, 0 }, Assert.Single(shared.CreateRangeReports(540)));
        Assert.Equal(new byte[][]
        {
            [0, 0xF8, 0x02, 0, 0, 0, 0, 0],
            [0, 0x81, 0x0B, 0, 0, 0, 0, 0],
        }, dfp.CreateRangeReports(200));
        Assert.Equal(new byte[][]
        {
            [0, 0xF8, 0x03, 0, 0, 0, 0, 0],
            [0, 0x81, 0x0B, 0, 0, 0, 0, 0],
        }, dfp.CreateRangeReports(900));
        Assert.False(dfp.IsRangeSupported(540));
        Assert.Throws<ArgumentOutOfRangeException>(() => dfp.CreateRangeReports(540));
    }

    [Fact]
    public void ProtocolProfileWithoutNativeFrictionUsesDamperEncoding()
    {
        WheelDefinition dfgt = ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT);
        var definition = new WheelDefinition(
            dfgt.Model,
            dfgt.DisplayName,
            dfgt.RevisionMatchers,
            dfgt.Presentations,
            dfgt.PreferredModeSwitch,
            dfgt.SteeringRange,
            dfgt.Capabilities & ~WheelCapabilities.NativeFriction,
            dfgt.ProtocolProfile with { SupportsNativeFriction = false });
        var protocol = new ClassicWheelProtocol(definition);
        var condition = new ConditionEffectDefinition(
            new EffectCommon(1_000, 0, 0, 10_000, 10_000, null),
            ForceEffectKind.Friction,
            0,
            -700,
            -700,
            10_000,
            10_000,
            0);
        Span<byte> report = stackalloc byte[protocol.ReportLength];

        protocol.WriteCondition(report, 1, FirmwareSlotOperation.Start, condition);

        Assert.Equal((byte)0x0C, report[2]);
    }

    [Theory]
    [InlineData(WheelModel.DrivingForceGT)]
    [InlineData(WheelModel.G25)]
    [InlineData(WheelModel.G27)]
    [InlineData(WheelModel.DrivingForcePro)]
    public void ProtocolRangeValidationAndEncodingAgreeWithWheelDefinition(WheelModel model)
    {
        WheelDefinition definition = ClassicWheelCatalog.GetDefinition(model);
        var protocol = new ClassicWheelProtocol(definition);

        for (int degrees = 0; degrees <= 901; degrees++)
        {
            bool supported = definition.SteeringRange.Supports(degrees);
            Assert.Equal(supported, protocol.IsRangeSupported(degrees));
            if (supported)
            {
                Assert.NotEmpty(protocol.CreateRangeReports(degrees));
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => protocol.CreateRangeReports(degrees));
            }
        }
    }

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
    public void WheelCandidateCodecRoundTripsBoundedV11Payload()
    {
        WheelCandidateInfo[] source =
        [
            new(7, WheelModel.G27, "G27 Racing Wheel", 0x1234, 0xC29B,
                "hid-path-g27", 7, IsSelected: true, IsReady: true),
            new(9, WheelModel.DrivingForceGT, "Driving Force GT", 0x1326, 0xC294,
                "hid-path-dfgt", 1, IsSelected: false, IsReady: false),
        ];

        byte[] payload = WheelCandidateCodec.Encode(source);

        Assert.True(WheelCandidateCodec.TryDecode(payload, out IReadOnlyList<WheelCandidateInfo>? decoded));
        Assert.Equal(source, decoded);
        Assert.Equal((ushort)1, IpcFrameCodec.MinorVersion);
        Assert.Equal((ushort)35, (ushort)IpcMessageType.QueryWheelCandidates);
        Assert.Equal((ushort)36, (ushort)IpcMessageType.SelectWheel);
    }

    [Fact]
    public void WheelCandidateCodecRejectsTruncationAndUnboundedLists()
    {
        byte[] payload = WheelCandidateCodec.Encode(
            [new WheelCandidateInfo(1, WheelModel.G25, "G25", 0x1201, 0xC299, "path", 1, false, false)]);

        Assert.False(WheelCandidateCodec.TryDecode(payload[..^1], out _));
        payload[0] = ClassicWheelCatalog.MaximumCandidates + 1;
        Assert.False(WheelCandidateCodec.TryDecode(payload, out _));
    }

    [Fact]
    public void DfgtFirmwareSlotsMatchGoldenVectors()
    {
        var protocol = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT));
        Span<byte> report = stackalloc byte[protocol.ReportLength];
        protocol.WriteConstant(report, 0, FirmwareSlotOperation.Start, 0);
        Assert.Equal(new byte[] { 0, 0x11, 0x00, 0x80, 0, 0, 0, 0 }, report.ToArray());
        protocol.WriteConstant(report, 0, FirmwareSlotOperation.Start, -10_000);
        Assert.Equal(new byte[] { 0, 0x11, 0x00, 0x01, 0, 0, 0, 0 }, report.ToArray());
        protocol.WriteConstant(report, 0, FirmwareSlotOperation.Start, 10_000);
        Assert.Equal(new byte[] { 0, 0x11, 0x00, 0xFF, 0, 0, 0, 0 }, report.ToArray());

        byte[] commands = { 0x13, 0x23, 0x43, 0x83 };
        for (int slot = 0; slot < 4; slot++)
        {
            protocol.WriteSlotStop(report, slot);
            Assert.Equal(commands[slot], report[1]);
        }

        protocol.WriteSpring(report, 1, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x21, 0x0B, 0x7F, 0x7F, 0, 0xFF, 0xFF }, report.ToArray());
        protocol.WriteSpring(report, 1, FirmwareSlotOperation.Start,
            0, 0, -1_500, -1_500, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x21, 0x0B, 0x7F, 0x7F, 0x11, 0xFF, 0xFF }, report.ToArray());
        protocol.WriteDamper(report, 2, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x41, 0x0C, 0x01, 0x01, 0x01, 0x01, 0xFF }, report.ToArray());
        protocol.WriteFriction(report, 3, FirmwareSlotOperation.Start,
            0, 0, -700, -700, 10_000, 10_000);
        Assert.Equal(new byte[] { 0, 0x81, 0x0E, 0x11, 0x11, 0xFF, 0x11, 0 }, report.ToArray());
        protocol.WriteDamper(report, 2, FirmwareSlotOperation.Start,
            int.MinValue, int.MaxValue, int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        Assert.Equal(new byte[] { 0, 0x41, 0x0C, 0x0F, 0x01, 0x0F, 0x01, 0xFF }, report.ToArray());
    }

    [Theory]
    [InlineData(WheelModel.DrivingForceGT)]
    [InlineData(WheelModel.G25)]
    [InlineData(WheelModel.G27)]
    [InlineData(WheelModel.DrivingForcePro)]
    public void EveryWheelUsesDirectConstantEncodingInEveryFirmwareSlot(WheelModel model)
    {
        var protocol = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(model));
        Span<byte> report = stackalloc byte[protocol.ReportLength];

        for (int slot = 0; slot < 4; slot++)
        {
            protocol.WriteConstant(report, slot, FirmwareSlotOperation.Start, 10_000);
            Assert.Equal(0x00, report[2]);
            Assert.Equal(0xFF, report[3 + slot]);
            Assert.All(report[3..(3 + slot)].ToArray(), static value => Assert.Equal(0, value));
            Assert.All(report[(4 + slot)..].ToArray(), static value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(WheelModel.DrivingForceGT)]
    [InlineData(WheelModel.G25)]
    [InlineData(WheelModel.G27)]
    public void ContinuousRangeWheelsAcceptInteriorValues(WheelModel model)
    {
        var protocol = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(model));

        Assert.True(protocol.IsRangeSupported(40));
        Assert.True(protocol.IsRangeSupported(540));
        Assert.True(protocol.IsRangeSupported(900));
    }

    [Theory]
    [InlineData(40, 0x28, 0x00)]
    [InlineData(540, 0x1C, 0x02)]
    [InlineData(900, 0x84, 0x03)]
    public void DfgtRangeMatchesCapturedExtendedCommand(int degrees, byte low, byte high)
    {
        var protocol = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT));
        Span<byte> report = stackalloc byte[protocol.ReportLength];

        byte[] range = Assert.Single(protocol.CreateRangeReports(degrees));

        Assert.Equal(new byte[] { 0, 0xF8, 0x81, low, high, 0, 0, 0 }, range);
    }

    [Fact]
    public void DfgtAutocenterMatchesCapturedBoundaryVectors()
    {
        var protocol = new ClassicWheelProtocol(ClassicWheelCatalog.GetDefinition(WheelModel.DrivingForceGT));
        Span<byte> report = stackalloc byte[protocol.ReportLength];

        protocol.WriteDisableAutocenter(report);
        Assert.Equal(new byte[] { 0, 0xF5, 0, 0, 0, 0, 0, 0 }, report.ToArray());
        protocol.WriteAutocenterParameters(report, 0);
        Assert.Equal(new byte[] { 0, 0xFE, 0x0D, 0, 0, 0, 0, 0 }, report.ToArray());
        protocol.WriteAutocenterParameters(report, 10_000);
        Assert.Equal(new byte[] { 0, 0xFE, 0x0D, 0x07, 0x07, 0xFF, 0, 0 }, report.ToArray());
        protocol.WriteEnableAutocenter(report);
        Assert.Equal(new byte[] { 0, 0x14, 0, 0, 0, 0, 0, 0 }, report.ToArray());
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
