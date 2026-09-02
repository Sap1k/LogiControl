// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

/// <summary>Separates stable physical-model identity from the current USB presentation.</summary>
public sealed record WheelIdentity(
    WheelDefinition Definition,
    WheelPresentationDefinition Presentation,
    ushort VendorId,
    ushort VersionNumber)
{
    public ushort PresentedProductId => Presentation.ProductId;

    public bool IsPreferredMode => Presentation.IsPreferred;
}
