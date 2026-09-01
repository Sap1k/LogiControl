// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace LogiControl.Broker;

public interface IMonotonicClock
{
    long GetMicroseconds();
}

public sealed class QpcMonotonicClock : IMonotonicClock
{
    private static readonly long Frequency = Stopwatch.Frequency > 0
        ? Stopwatch.Frequency
        : throw new InvalidOperationException("The high-resolution performance counter has no valid frequency.");

    public long GetMicroseconds() => ScaleTimestampToMicroseconds(Stopwatch.GetTimestamp(), Frequency);

    internal static long ScaleTimestampToMicroseconds(long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        long seconds = timestamp / frequency;
        long remainder = timestamp % frequency;
        return checked(seconds * 1_000_000L + remainder * 1_000_000L / frequency);
    }
}
