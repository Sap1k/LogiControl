// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public readonly record struct SteeringCalibrationObservation(
    int SampleCount,
    uint Minimum,
    uint Maximum,
    uint Last);

public interface IHidCalibrationMonitor
{
    ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
        HidDeviceSnapshot device,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
