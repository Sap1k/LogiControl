// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;

namespace LogiControl.Protocol;

public sealed class SteeringRangeDefinition
{
    private readonly ReadOnlyCollection<int> discreteDegrees;

    public SteeringRangeDefinition(
        int minimumDegrees,
        int maximumDegrees,
        IEnumerable<int>? discreteDegrees = null)
    {
        if (minimumDegrees <= 0 || maximumDegrees < minimumDegrees)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDegrees));
        }

        int[] values = discreteDegrees?.Distinct().Order().ToArray() ?? [];
        if (values.Any(value => value < minimumDegrees || value > maximumDegrees) ||
            values.Length > 0 && (values[0] != minimumDegrees || values[^1] != maximumDegrees))
        {
            throw new ArgumentException(
                "Discrete steering ranges must be unique, bounded, and include both endpoints.",
                nameof(discreteDegrees));
        }

        MinimumDegrees = minimumDegrees;
        MaximumDegrees = maximumDegrees;
        this.discreteDegrees = Array.AsReadOnly(values);
    }

    public int MinimumDegrees { get; }

    public int MaximumDegrees { get; }

    public IReadOnlyList<int> DiscreteDegrees => discreteDegrees;

    public bool IsDiscrete => discreteDegrees.Count > 0;

    public bool Supports(int degrees) => IsDiscrete
        ? discreteDegrees.Contains(degrees)
        : degrees >= MinimumDegrees && degrees <= MaximumDegrees;
}
