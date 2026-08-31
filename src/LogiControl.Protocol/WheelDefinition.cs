// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

public sealed class WheelDefinition
{
    private readonly ushort[] presentationProductIds;
    private readonly LogitechCommand[] nativeModeSwitchSequence;

    public WheelDefinition(
        WheelModel model,
        string displayName,
        WheelRevisionMatcher revisionMatcher,
        ushort nativeProductId,
        IEnumerable<ushort> presentationProductIds,
        IEnumerable<LogitechCommand> nativeModeSwitchSequence,
        WheelCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(presentationProductIds);
        ArgumentNullException.ThrowIfNull(nativeModeSwitchSequence);

        Model = model;
        DisplayName = displayName;
        RevisionMatcher = revisionMatcher;
        NativeProductId = nativeProductId;
        this.presentationProductIds = presentationProductIds.Distinct().ToArray();
        this.nativeModeSwitchSequence = nativeModeSwitchSequence.ToArray();
        Capabilities = capabilities;

        if (this.presentationProductIds.Length == 0)
        {
            throw new ArgumentException("At least one presentation PID is required.", nameof(presentationProductIds));
        }
    }

    public WheelModel Model { get; }

    public string DisplayName { get; }

    public WheelRevisionMatcher RevisionMatcher { get; }

    public ushort NativeProductId { get; }

    public IReadOnlyList<ushort> PresentationProductIds => presentationProductIds;

    public IReadOnlyList<LogitechCommand> NativeModeSwitchSequence => nativeModeSwitchSequence;

    public WheelCapabilities Capabilities { get; }

    public bool CanPresentAs(ushort productId) =>
        Array.IndexOf(presentationProductIds, productId) >= 0;
}
