// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.Tracing;

namespace LogiControl.Broker;

[EventSource(Name = "LogiControl-Broker")]
internal sealed class BrokerEventSource : EventSource
{
    public static BrokerEventSource Log { get; } = new();

    [Event(1, Level = EventLevel.Verbose)]
    public void MixerTick(long scheduledMicroseconds, long actualMicroseconds, long computationMicroseconds,
        long missedDeadlines, int activeEffects) =>
        WriteEvent(1, scheduledMicroseconds, actualMicroseconds, computationMicroseconds, missedDeadlines, activeEffects);

    [Event(2, Level = EventLevel.Verbose)]
    public void CommandApplied(long latencyMicroseconds) => WriteEvent(2, latencyMicroseconds);

    [Event(3, Level = EventLevel.Verbose)]
    public void HidWrite(long publicationToSubmissionMicroseconds, long publicationToCompletionMicroseconds,
        long writeDurationMicroseconds) =>
        WriteEvent(3, publicationToSubmissionMicroseconds, publicationToCompletionMicroseconds, writeDurationMicroseconds);

    [Event(4, Level = EventLevel.Error)]
    public void HidWriteFailed(string exceptionType) => WriteEvent(4, exceptionType);

    [Event(5, Level = EventLevel.Informational)]
    public void DeviceLifecycle(string previous, string current, string reason) =>
        WriteEvent(5, previous, current, reason);

    [Event(6, Level = EventLevel.Warning)]
    public void ReadOnlyPresentation(string productId, string revision, string reason) =>
        WriteEvent(6, productId, revision, reason);
}
