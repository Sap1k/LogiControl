// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

public sealed class WheelDefinition
{
    private readonly WheelRevisionMatcher[] revisionMatchers;
    private readonly WheelPresentationDefinition[] presentations;

    public WheelDefinition(
        WheelModel model,
        string displayName,
        IEnumerable<WheelRevisionMatcher> revisionMatchers,
        IEnumerable<WheelPresentationDefinition> presentations,
        ModeSwitchPlan preferredModeSwitch,
        SteeringRangeDefinition steeringRange,
        WheelCapabilities capabilities,
        WheelProtocolProfile protocolProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(revisionMatchers);
        ArgumentNullException.ThrowIfNull(presentations);
        ArgumentNullException.ThrowIfNull(preferredModeSwitch);
        ArgumentNullException.ThrowIfNull(steeringRange);
        ArgumentNullException.ThrowIfNull(protocolProfile);

        this.revisionMatchers = revisionMatchers.ToArray();
        this.presentations = presentations.Select(static presentation => presentation.Validate()).ToArray();
        if (this.revisionMatchers.Length == 0)
        {
            throw new ArgumentException("At least one revision matcher is required.", nameof(revisionMatchers));
        }

        if (this.presentations.Length == 0 ||
            this.presentations.Select(static value => value.ProductId).Distinct().Count() != this.presentations.Length)
        {
            throw new ArgumentException("Presentation PIDs must be non-empty and unique.", nameof(presentations));
        }

        WheelPresentationDefinition[] preferred = this.presentations.Where(static value => value.IsPreferred).ToArray();
        if (preferred.Length != 1)
        {
            throw new ArgumentException("Exactly one preferred presentation is required.", nameof(presentations));
        }

        if (protocolProfile.NativeReportLayout != preferred[0].ReportLayout)
        {
            throw new ArgumentException("The protocol and preferred presentation report layouts must match.", nameof(protocolProfile));
        }

        Model = model;
        DisplayName = displayName;
        PreferredModeSwitch = preferredModeSwitch;
        SteeringRange = steeringRange;
        Capabilities = capabilities;
        ProtocolProfile = protocolProfile;
        PreferredPresentation = preferred[0];
    }

    public WheelModel Model { get; }

    public string DisplayName { get; }

    public IReadOnlyList<WheelRevisionMatcher> RevisionMatchers => revisionMatchers;

    public IReadOnlyList<WheelPresentationDefinition> Presentations => presentations;

    public WheelPresentationDefinition PreferredPresentation { get; }

    public ushort PreferredProductId => PreferredPresentation.ProductId;

    public ModeSwitchPlan PreferredModeSwitch { get; }

    public SteeringRangeDefinition SteeringRange { get; }

    public int MinimumRangeDegrees => SteeringRange.MinimumDegrees;

    public int MaximumRangeDegrees => SteeringRange.MaximumDegrees;

    public WheelCapabilities Capabilities { get; }

    public WheelProtocolProfile ProtocolProfile { get; }

    public bool MatchesRevision(ushort versionNumber) =>
        revisionMatchers.Any(matcher => matcher.Matches(versionNumber));

    public bool TryGetPresentation(ushort productId, out WheelPresentationDefinition? presentation)
    {
        presentation = presentations.FirstOrDefault(value => value.ProductId == productId);
        return presentation is not null;
    }

    public bool CanPresentAs(ushort productId) => TryGetPresentation(productId, out _);
}
