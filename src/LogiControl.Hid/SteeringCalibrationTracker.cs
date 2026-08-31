// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public sealed class SteeringCalibrationTracker
{
    private readonly uint movementThreshold;
    private readonly uint motionJitterThreshold;
    private readonly uint centerLowThreshold;
    private readonly uint centerHighThreshold;

    public SteeringCalibrationTracker(uint logicalMinimum, uint logicalMaximum)
    {
        if (logicalMaximum <= logicalMinimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalMaximum),
                "The steering logical maximum must be greater than its minimum.");
        }

        ulong valueCount = (ulong)logicalMaximum - logicalMinimum + 1;
        movementThreshold = (uint)Math.Max(16UL, valueCount / 32);
        motionJitterThreshold = (uint)Math.Max(4UL, valueCount / 1024);
        centerLowThreshold = logicalMinimum + (uint)(valueCount * 7 / 16);
        centerHighThreshold = logicalMinimum + (uint)(valueCount * 9 / 16) - 1;
    }

    public int SampleCount { get; private set; }

    public uint Minimum { get; private set; } = uint.MaxValue;

    public uint Maximum { get; private set; }

    public uint Last { get; private set; }

    public bool HasMoved => SampleCount > 1 && Maximum - Minimum >= movementThreshold;

    public bool IsNearCenter =>
        SampleCount > 0 && Last >= centerLowThreshold && Last <= centerHighThreshold;

    public TimeSpan LastMotionAt { get; private set; }

    public void Observe(uint value, TimeSpan observedAt)
    {
        if (SampleCount == 0 || AbsoluteDifference(value, Last) >= motionJitterThreshold)
        {
            LastMotionAt = observedAt;
        }
        ++SampleCount;
        Last = value;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
    }

    public bool IsComplete(TimeSpan now, TimeSpan stableDuration) =>
        HasMoved && IsNearCenter && now - LastMotionAt >= stableDuration;

    public TimeSpan StabilityRemaining(TimeSpan now, TimeSpan stableDuration)
    {
        if (!HasMoved || !IsNearCenter) return Timeout.InfiniteTimeSpan;
        TimeSpan remaining = stableDuration - (now - LastMotionAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public SteeringCalibrationObservation Snapshot() => new(
        SampleCount,
        SampleCount == 0 ? 0 : Minimum,
        Maximum,
        Last);

    private static uint AbsoluteDifference(uint left, uint right) =>
        left >= right ? left - right : right - left;
}
