// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.DeviceAgent;

public enum LegacyBrokerDeviceState : uint
{
    Disconnected = 0,
    Ready = 1,
    ProfileActive = 2,
    GameActive = 3,
    FailSafe = 4,
    Faulted = 5,
}

public sealed record LegacyBrokerStatus(
    LegacyBrokerDeviceState State,
    bool Connected,
    bool FfbClientConnected,
    bool ControlClientConnected,
    uint FailSafeCount,
    int RangeDegrees,
    int OverallGain,
    int BoundaryForce,
    int LastResult,
    string ActiveProfileId);
