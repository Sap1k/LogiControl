// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Hid.Tests;

public sealed class HidContractTests
{
    [Fact]
    public void SnapshotPreservesReportMetadata()
    {
        HidDeviceSnapshot snapshot = Snapshot();
        Assert.Equal((ushort)8, snapshot.OutputReportByteLength);
        Assert.Equal((ushort)0x1301, snapshot.VersionNumber);
        Assert.True(snapshot.IsJoystick);
    }

    [Fact]
    public void UnnumberedReportIsFramedAndShortReportIsRejected()
    {
        var command = new LogitechCommand(0xF8, 0x0A, 0, 0, 0, 0, 0);
        byte[] report = HidOutputReportFormatter.FormatUnnumberedCommand(command, 10);
        Assert.Equal(new byte[] { 0, 0xF8, 0x0A, 0, 0, 0, 0, 0, 0, 0 }, report);
        Assert.Throws<ArgumentOutOfRangeException>(() => HidOutputReportFormatter.FormatUnnumberedCommand(command, 7));
    }

    [Fact]
    public void CalibrationRequiresMovementFollowedByStableCenter()
    {
        var calibration = new SteeringCalibrationTracker(0, 1023);
        calibration.Observe(0, TimeSpan.Zero);
        calibration.Observe(583, TimeSpan.FromMilliseconds(100));
        calibration.Observe(509, TimeSpan.FromMilliseconds(200));
        Assert.False(calibration.IsComplete(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(350)));
        calibration.Observe(511, TimeSpan.FromMilliseconds(450));
        Assert.True(calibration.IsComplete(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(350)));
        Assert.Equal(new SteeringCalibrationObservation(4, 0, 583, 511), calibration.Snapshot());
    }

    [Fact]
    public void CalibrationRejectsStationaryAndOffCenterInput()
    {
        var stationary = new SteeringCalibrationTracker(0, 1023);
        stationary.Observe(509, TimeSpan.Zero);
        stationary.Observe(511, TimeSpan.FromSeconds(1));
        Assert.False(stationary.IsComplete(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(350)));

        var departedCenter = new SteeringCalibrationTracker(0, 1023);
        departedCenter.Observe(0, TimeSpan.Zero);
        departedCenter.Observe(509, TimeSpan.FromMilliseconds(100));
        departedCenter.Observe(300, TimeSpan.FromMilliseconds(300));
        Assert.False(departedCenter.IsComplete(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(350)));
    }

    private static HidDeviceSnapshot Snapshot() => new(
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
}
