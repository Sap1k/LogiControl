// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

var tests = new (string Name, Action Run)[]
{
    ("C294 DFGT identity", IdentifiesCompatibilityModeDfgt),
    ("DFGT native switch vectors", EncodesDfgtNativeSwitch),
    ("Classic revision ordering", IdentifiesOverlappingClassicRevisions),
    ("Unknown C294 is rejected", RejectsUnknownCompatibilityDevice),
    ("Non-Logitech device is rejected", RejectsOtherVendor),
    ("Command storage is immutable", ProtectsCommandBytes),
};

var failures = new List<string>();

foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

foreach (string failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static void IdentifiesCompatibilityModeDfgt()
{
    bool found = ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity);

    Require(found, "Expected the device to be identified.");
    WheelIdentity actual = identity ?? throw new InvalidOperationException("Identity output was null.");
    Require(actual.Definition.Model == WheelModel.DrivingForceGT, "Expected a physical DFGT.");
    Require(actual.PresentedProductId == 0xC294, "Expected compatibility-mode PID to be retained.");
    Require(!actual.IsNativeMode, "C294 must not be treated as DFGT native mode.");
}

static void EncodesDfgtNativeSwitch()
{
    ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x1301, out WheelIdentity? identity);
    IReadOnlyList<LogitechCommand> commands = identity!.Definition.NativeModeSwitchSequence;

    Require(commands.Count == 2, "Expected two DFGT mode-switch commands.");
    Require(commands[0].Bytes.Span.SequenceEqual(new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 }), "First vector differs.");
    Require(commands[1].Bytes.Span.SequenceEqual(new byte[] { 0xF8, 0x09, 0x03, 0x01, 0, 0, 0 }), "Second vector differs.");
    Require(identity.Definition.NativeProductId == 0xC29A, "Expected native PID C29A.");
}

static void IdentifiesOverlappingClassicRevisions()
{
    var cases = new (ushort Revision, WheelModel Expected)[]
    {
        (0x1234, WheelModel.G27),
        (0x1224, WheelModel.G25),
        (0x1210, WheelModel.DrivingForcePro),
    };

    foreach ((ushort revision, WheelModel expected) in cases)
    {
        bool found = ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, revision, out WheelIdentity? identity);
        Require(found && identity?.Definition.Model == expected, $"Revision {revision:X4} did not identify as {expected}.");
    }
}

static void RejectsUnknownCompatibilityDevice()
{
    bool found = ClassicWheelCatalog.TryIdentify(0x046D, 0xC294, 0x2000, out WheelIdentity? identity);
    Require(!found && identity is null, "An unknown C294 revision must remain unidentified.");
}

static void RejectsOtherVendor()
{
    bool found = ClassicWheelCatalog.TryIdentify(0x1234, 0xC294, 0x1301, out WheelIdentity? identity);
    Require(!found && identity is null, "Vendor ID is part of identity.");
}

static void ProtectsCommandBytes()
{
    var source = new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 };
    var command = new LogitechCommand(source);
    source[0] = 0;
    byte[] copy = command.ToArray();
    copy[1] = 0;

    Require(command.Bytes.Span[0] == 0xF8, "Constructor retained mutable caller storage.");
    Require(command.Bytes.Span[1] == 0x0A, "ToArray exposed internal storage.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
