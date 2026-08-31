// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Hid;

var snapshot = new HidDeviceSnapshot(
    "test-path",
    "USB\\VID_046D&PID_C294\\TEST",
    0x046D,
    0xC294,
    0x1301,
    0x01,
    0x04,
    16,
    8,
    7);

if (snapshot.OutputReportByteLength != 8 || snapshot.VersionNumber != 0x1301 || !snapshot.IsJoystick)
{
    Console.Error.WriteLine("FAIL HID snapshot preserves report metadata.");
    return 1;
}

Console.WriteLine("PASS HID snapshot preserves report metadata.");

var command = new LogiControl.Protocol.LogitechCommand(0xF8, 0x0A, 0, 0, 0, 0, 0);
byte[] report = HidOutputReportFormatter.FormatUnnumberedCommand(command, 10);
if (!report.AsSpan().SequenceEqual(new byte[] { 0, 0xF8, 0x0A, 0, 0, 0, 0, 0, 0, 0 }))
{
    Console.Error.WriteLine("FAIL Unnumbered output report framing.");
    return 1;
}

try
{
    HidOutputReportFormatter.FormatUnnumberedCommand(command, 7);
    Console.Error.WriteLine("FAIL Short output report was accepted.");
    return 1;
}
catch (ArgumentOutOfRangeException)
{
    Console.WriteLine("PASS Unnumbered report framing and short-report refusal.");
}

var calibration = new SteeringCalibrationTracker(0, 1023);
calibration.Observe(0, TimeSpan.Zero);
calibration.Observe(583, TimeSpan.FromMilliseconds(100));
calibration.Observe(509, TimeSpan.FromMilliseconds(200));
if (calibration.IsComplete(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(350)))
{
    Console.Error.WriteLine("FAIL Calibration completed before center was stable.");
    return 1;
}
calibration.Observe(511, TimeSpan.FromMilliseconds(450));
if (!calibration.IsComplete(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(350)))
{
    Console.Error.WriteLine("FAIL Center jitter incorrectly reset calibration stability.");
    return 1;
}
if (calibration.Snapshot() != new SteeringCalibrationObservation(4, 0, 583, 511))
{
    Console.Error.WriteLine("FAIL Calibration movement tracking.");
    return 1;
}
Console.WriteLine("PASS Calibration requires movement followed by a stable center.");

var stationary = new SteeringCalibrationTracker(0, 1023);
stationary.Observe(509, TimeSpan.Zero);
stationary.Observe(511, TimeSpan.FromSeconds(1));
if (stationary.IsComplete(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(350)))
{
    Console.Error.WriteLine("FAIL A stationary centered wheel was accepted as calibrated.");
    return 1;
}

var departedCenter = new SteeringCalibrationTracker(0, 1023);
departedCenter.Observe(0, TimeSpan.Zero);
departedCenter.Observe(509, TimeSpan.FromMilliseconds(100));
departedCenter.Observe(300, TimeSpan.FromMilliseconds(300));
if (departedCenter.IsComplete(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(350)))
{
    Console.Error.WriteLine("FAIL A wheel outside the center window was accepted as calibrated.");
    return 1;
}
Console.WriteLine("PASS Calibration rejects stationary and off-center input.");

return 0;
