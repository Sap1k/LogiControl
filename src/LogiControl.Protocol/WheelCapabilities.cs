// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

[Flags]
public enum WheelCapabilities
{
    None = 0,
    NativeModeSwitch = 1 << 0,
    SteeringRange = 1 << 1,
    Autocenter = 1 << 2,
    ForceFeedback = 1 << 3,
    NativeFriction = 1 << 4,
}
