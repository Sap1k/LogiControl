// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Broker;

public sealed class RuntimeTelemetry
{
    public static ReadOnlySpan<long> BucketBoundsMicroseconds =>
        [10, 25, 50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000];

    private static readonly long[] BoundsMicroseconds = BucketBoundsMicroseconds.ToArray();
    private readonly long[] earlyJitterBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] lateJitterBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] absoluteJitterBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] computationBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] commandToMixBuckets = new long[BoundsMicroseconds.Length + 1];
    private long ticks;
    private long missedDeadlines;
    private long overruns;
    private long commandCount;
    private long mixerAllocatedBytes;
    private long ticksWithAllocations;

    public long Ticks => Interlocked.Read(ref ticks);

    public long MissedDeadlines => Interlocked.Read(ref missedDeadlines);

    public long Overruns => Interlocked.Read(ref overruns);

    public long CommandCount => Interlocked.Read(ref commandCount);

    public long MixerAllocatedBytes => Interlocked.Read(ref mixerAllocatedBytes);

    public long TicksWithAllocations => Interlocked.Read(ref ticksWithAllocations);

    public ReadOnlyMemory<long> AbsoluteJitterBuckets => absoluteJitterBuckets;

    public ReadOnlyMemory<long> EarlyJitterBuckets => earlyJitterBuckets;

    public ReadOnlyMemory<long> LateJitterBuckets => lateJitterBuckets;

    public ReadOnlyMemory<long> ComputationBuckets => computationBuckets;

    public ReadOnlyMemory<long> CommandToMixBuckets => commandToMixBuckets;

    internal void RecordTick(
        long signedJitterMicroseconds,
        long computationMicroseconds,
        long missed,
        long allocatedBytes)
    {
        Interlocked.Increment(ref ticks);
        if (missed > 0)
        {
            Interlocked.Add(ref missedDeadlines, missed);
        }

        if (computationMicroseconds > EffectRuntime.PeriodMicroseconds)
        {
            Interlocked.Increment(ref overruns);
        }

        if (allocatedBytes > 0)
        {
            Interlocked.Add(ref mixerAllocatedBytes, allocatedBytes);
            Interlocked.Increment(ref ticksWithAllocations);
        }

        IncrementBucket(absoluteJitterBuckets, Math.Abs(signedJitterMicroseconds));
        IncrementBucket(signedJitterMicroseconds < 0 ? earlyJitterBuckets : lateJitterBuckets,
            Math.Abs(signedJitterMicroseconds));
        IncrementBucket(computationBuckets, computationMicroseconds);
    }

    internal void RecordCommand(long commandToMixMicroseconds)
    {
        Interlocked.Increment(ref commandCount);
        IncrementBucket(commandToMixBuckets, Math.Max(0, commandToMixMicroseconds));
    }

    private static void IncrementBucket(long[] buckets, long value)
    {
        int index = 0;
        while (index < BoundsMicroseconds.Length && value > BoundsMicroseconds[index])
        {
            index++;
        }

        Interlocked.Increment(ref buckets[index]);
    }
}

public sealed class OutputTelemetry
{
    private static readonly long[] BoundsMicroseconds = RuntimeTelemetry.BucketBoundsMicroseconds.ToArray();
    private readonly long[] publicationToSubmissionBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] publicationToCompletionBuckets = new long[BoundsMicroseconds.Length + 1];
    private readonly long[] hidDurationBuckets = new long[BoundsMicroseconds.Length + 1];
    private long desiredPublications;
    private long barrierPublications;
    private long coalescedReports;
    private long hidSubmissions;
    private long hidCompletions;
    private long writeFailures;
    private long maximumQueueDepth;

    public long DesiredPublications => Interlocked.Read(ref desiredPublications);
    public long BarrierPublications => Interlocked.Read(ref barrierPublications);
    public long CoalescedReports => Interlocked.Read(ref coalescedReports);
    public long HidSubmissions => Interlocked.Read(ref hidSubmissions);
    public long HidCompletions => Interlocked.Read(ref hidCompletions);
    public long WriteFailures => Interlocked.Read(ref writeFailures);
    public long MaximumQueueDepth => Interlocked.Read(ref maximumQueueDepth);
    public ReadOnlyMemory<long> PublicationToSubmissionBuckets => publicationToSubmissionBuckets;
    public ReadOnlyMemory<long> PublicationToCompletionBuckets => publicationToCompletionBuckets;
    public ReadOnlyMemory<long> HidDurationBuckets => hidDurationBuckets;

    internal void RecordDesiredPublication() => Interlocked.Increment(ref desiredPublications);
    internal void RecordBarrierPublication(long queueDepth)
    {
        Interlocked.Increment(ref barrierPublications);
        long maximum = Interlocked.Read(ref maximumQueueDepth);
        while (queueDepth > maximum)
        {
            long observed = Interlocked.CompareExchange(ref maximumQueueDepth, queueDepth, maximum);
            if (observed == maximum)
            {
                break;
            }

            maximum = observed;
        }
    }

    internal void RecordCoalesced() => Interlocked.Increment(ref coalescedReports);

    internal void RecordSubmission(long publicationToSubmissionMicroseconds)
    {
        Interlocked.Increment(ref hidSubmissions);
        IncrementBucket(publicationToSubmissionBuckets, publicationToSubmissionMicroseconds);
    }

    internal void RecordCompletion(long publicationToCompletionMicroseconds, long hidDurationMicroseconds)
    {
        Interlocked.Increment(ref hidCompletions);
        IncrementBucket(publicationToCompletionBuckets, publicationToCompletionMicroseconds);
        IncrementBucket(hidDurationBuckets, hidDurationMicroseconds);
    }

    internal void RecordFailure() => Interlocked.Increment(ref writeFailures);

    private static void IncrementBucket(long[] buckets, long value)
    {
        int index = 0;
        while (index < BoundsMicroseconds.Length && value > BoundsMicroseconds[index])
        {
            index++;
        }

        Interlocked.Increment(ref buckets[index]);
    }
}
