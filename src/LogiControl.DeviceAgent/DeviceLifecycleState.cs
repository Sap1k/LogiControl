// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.DeviceAgent;

public enum DeviceLifecycleState
{
    Absent,
    Observed,
    Identified,
    AwaitingSwitchAuthorization,
    Switching,
    AwaitingNativeMode,
    NativeModeReady,
    Attached,
    Faulted,
    Calibrating,
}
