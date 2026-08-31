// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

/// <summary>Separates stable physical-model identity from the current USB presentation.</summary>
public sealed record WheelIdentity(
    WheelDefinition Definition,
    ushort VendorId,
    ushort PresentedProductId,
    ushort VersionNumber)
{
    public bool IsNativeMode => PresentedProductId == Definition.NativeProductId;
}
