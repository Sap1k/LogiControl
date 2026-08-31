// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

public readonly record struct WheelRevisionMatcher(ushort Mask, ushort Expected)
{
    public bool Matches(ushort versionNumber) =>
        (versionNumber & Mask) == Expected;
}
