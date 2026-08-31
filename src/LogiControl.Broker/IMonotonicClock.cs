// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace LogiControl.Broker;

public interface IMonotonicClock
{
    long GetMicroseconds();
}

public sealed class QpcMonotonicClock : IMonotonicClock
{
    public long GetMicroseconds() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
}
