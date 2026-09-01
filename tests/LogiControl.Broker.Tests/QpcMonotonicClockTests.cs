// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Broker.Tests;

public sealed class QpcMonotonicClockTests
{
    [Fact]
    public void ScalingRemainsCorrectAcrossFormerMultiplicationOverflowBoundary()
    {
        const long frequency = 10_000_000;
        long boundary = long.MaxValue / 1_000_000L;
        long[] timestamps = { boundary - 1, boundary, boundary + 1, boundary + frequency };
        long previous = -1;

        foreach (long timestamp in timestamps)
        {
            long actual = QpcMonotonicClock.ScaleTimestampToMicroseconds(timestamp, frequency);
            long expected = (long)((Int128)timestamp * 1_000_000 / frequency);
            Assert.Equal(expected, actual);
            Assert.True(actual >= previous);
            previous = actual;
        }
    }

    [Fact]
    public void ScalingRejectsInvalidCounterInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QpcMonotonicClock.ScaleTimestampToMicroseconds(-1, 10_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QpcMonotonicClock.ScaleTimestampToMicroseconds(1, 0));
    }
}
