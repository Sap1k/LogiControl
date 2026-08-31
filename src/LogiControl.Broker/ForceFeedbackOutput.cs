// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Threading.Channels;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public interface IForceFeedbackOutputSink : IDisposable
{
    WaitHandle Faulted { get; }

    void PublishSoftwareForce(int force);

    void PublishConditionChange(ConditionSlotChange change);

    void PublishBarrier(ReadOnlySpan<byte> report);
}

public sealed class NullForceFeedbackOutputSink : IForceFeedbackOutputSink
{
    private readonly ManualResetEvent faulted = new(false);

    public WaitHandle Faulted => faulted;

    public void PublishSoftwareForce(int force)
    {
    }

    public void PublishConditionChange(ConditionSlotChange change)
    {
    }

    public void PublishBarrier(ReadOnlySpan<byte> report)
    {
    }

    public void Dispose() => faulted.Dispose();
}

public sealed class SwitchableForceFeedbackOutputSink : IForceFeedbackOutputSink
{
    private readonly AutoResetEvent runtimeFaulted = new(false);
    private readonly Channel<CoalescingHidOutputPump> deviceFaults = Channel.CreateUnbounded<CoalescingHidOutputPump>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private IForceFeedbackOutputSink? current;
    private RegisteredWaitHandle? faultRegistration;
    private OutputTelemetry? lastTelemetry;
    private int disposed;

    public WaitHandle Faulted => runtimeFaulted;

    public bool IsAttached => Volatile.Read(ref current) is not null;

    public OutputTelemetry? Telemetry =>
        (Volatile.Read(ref current) as CoalescingHidOutputPump)?.Telemetry ?? lastTelemetry;

    public void Attach(CoalescingHidOutputPump pump)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref current, pump, null) is not null)
        {
            throw new InvalidOperationException("A HID output pump is already attached.");
        }

        lastTelemetry = pump.Telemetry;
        faultRegistration = ThreadPool.RegisterWaitForSingleObject(
            pump.Faulted,
            static (state, _) =>
            {
                var registration = (FaultRegistration)state!;
                registration.Owner.HandlePumpFault(registration.Pump);
            },
            new FaultRegistration(this, pump),
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
    }

    public CoalescingHidOutputPump? Detach()
    {
        RegisteredWaitHandle? registration = Interlocked.Exchange(ref faultRegistration, null);
        _ = registration?.Unregister(null);
        return Interlocked.Exchange(ref current, null) as CoalescingHidOutputPump;
    }

    public ValueTask<CoalescingHidOutputPump> WaitForDeviceFaultAsync(CancellationToken cancellationToken = default) =>
        deviceFaults.Reader.ReadAsync(cancellationToken);

    public void PublishSoftwareForce(int force) =>
        Volatile.Read(ref current)?.PublishSoftwareForce(force);

    public void PublishConditionChange(ConditionSlotChange change) =>
        Volatile.Read(ref current)?.PublishConditionChange(change);

    public void PublishBarrier(ReadOnlySpan<byte> report) =>
        Volatile.Read(ref current)?.PublishBarrier(report);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        deviceFaults.Writer.TryComplete();
        Detach()?.Dispose();
        runtimeFaulted.Dispose();
    }

    private void HandlePumpFault(CoalescingHidOutputPump pump)
    {
        if (Interlocked.CompareExchange(ref current, null, pump) != pump)
        {
            return;
        }

        Interlocked.Exchange(ref faultRegistration, null);
        runtimeFaulted.Set();
        deviceFaults.Writer.TryWrite(pump);
    }

    private sealed record FaultRegistration(
        SwitchableForceFeedbackOutputSink Owner,
        CoalescingHidOutputPump Pump);
}

public sealed class CoalescingHidOutputPump : IForceFeedbackOutputSink
{
    private readonly IHidTransport transport;
    private readonly IMonotonicClock clock;
    private readonly bool profileEvents;
    private readonly AutoResetEvent wake = new(false);
    private readonly ManualResetEvent faulted = new(false);
    private readonly ManualResetEvent shutdown = new(false);
    private readonly ConcurrentQueue<PendingReport> barriers = new();
    private readonly Thread worker;
    private int desiredForce;
    private long desiredPublishedAt;
    private long desiredVersion;
    private long submittedVersion;
    private Exception? failure;
    private bool disposed;
    private int transportClosed;

    public CoalescingHidOutputPump(IHidTransport transport, IMonotonicClock? clock = null,
        bool profileEvents = false)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.clock = clock ?? new QpcMonotonicClock();
        this.profileEvents = profileEvents;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "LogiControl HID output",
            Priority = ThreadPriority.AboveNormal,
        };
        worker.Start();
    }

    public WaitHandle Faulted => faulted;

    public long CoalescedReports => Telemetry.CoalescedReports;

    public OutputTelemetry Telemetry { get; } = new();

    public Exception? Failure => failure;

    public void PublishSoftwareForce(int force)
    {
        int clamped = Math.Clamp(force, -10_000, 10_000);
        Volatile.Write(ref desiredForce, clamped);
        Volatile.Write(ref desiredPublishedAt, clock.GetMicroseconds());
        Telemetry.RecordDesiredPublication();
        long prior = Interlocked.Read(ref desiredVersion);
        long next = Interlocked.Increment(ref desiredVersion);
        if (prior > Interlocked.Read(ref submittedVersion))
        {
            Telemetry.RecordCoalesced();
        }

        if (next > 0)
        {
            wake.Set();
        }
    }

    public void PublishConditionChange(ConditionSlotChange change)
    {
        Span<byte> report = stackalloc byte[DfgtForceFeedbackReports.ReportLength];
        if (change.Change == ConditionChangeKind.Stop)
        {
            DfgtForceFeedbackReports.WriteSlotStop(report, change.Slot);
        }
        else
        {
            ConditionEffectDefinition definition = change.Definition ??
                throw new ArgumentException("A start or update requires condition parameters.", nameof(change));
            FirmwareSlotOperation operation = change.Change == ConditionChangeKind.Start
                ? FirmwareSlotOperation.Start
                : FirmwareSlotOperation.Update;
            switch (definition.Kind)
            {
                case ForceEffectKind.Spring:
                    DfgtForceFeedbackReports.WriteSpring(report, change.Slot, operation, definition.Offset,
                        definition.DeadBand, definition.NegativeCoefficient, definition.PositiveCoefficient,
                        definition.NegativeSaturation, definition.PositiveSaturation);
                    break;
                case ForceEffectKind.Friction:
                    DfgtForceFeedbackReports.WriteFriction(report, change.Slot, operation, definition.Offset,
                        definition.DeadBand, definition.NegativeCoefficient, definition.PositiveCoefficient,
                        definition.NegativeSaturation, definition.PositiveSaturation);
                    break;
                case ForceEffectKind.Damper:
                case ForceEffectKind.Inertia:
                    DfgtForceFeedbackReports.WriteDamper(report, change.Slot, operation, definition.Offset,
                        definition.DeadBand, definition.NegativeCoefficient, definition.PositiveCoefficient,
                        definition.NegativeSaturation, definition.PositiveSaturation);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), "Unsupported condition effect.");
            }
        }

        PublishBarrier(report);
    }

    public void PublishBarrier(ReadOnlySpan<byte> report)
    {
        EnqueueBarrier(report, null);
    }

    public async ValueTask PublishBarrierAndWaitAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueBarrier(report.Span, completion);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Set();
        wake.Set();
        worker.Join();
        while (barriers.TryDequeue(out PendingReport pending))
        {
            pending.Completion?.TrySetException(new ObjectDisposedException(nameof(CoalescingHidOutputPump)));
        }
        shutdown.Dispose();
        wake.Dispose();
        faulted.Dispose();
        CloseTransport();
    }

    private void Run()
    {
        var waits = new WaitHandle[] { wake, shutdown };
        var constantReport = new byte[DfgtForceFeedbackReports.ReportLength];
        try
        {
            while (WaitHandle.WaitAny(waits) == 0)
            {
                while (barriers.TryDequeue(out PendingReport barrier))
                {
                    try
                    {
                        Write(barrier.Report, barrier.PublishedAtMicroseconds);
                        barrier.Completion?.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        barrier.Completion?.TrySetException(exception);
                        throw;
                    }
                }

                long version = Interlocked.Read(ref desiredVersion);
                if (version != submittedVersion)
                {
                    DfgtForceFeedbackReports.WriteConstant(constantReport, 0, FirmwareSlotOperation.Update, Volatile.Read(ref desiredForce));
                    Write(constantReport, Volatile.Read(ref desiredPublishedAt));
                    Interlocked.Exchange(ref submittedVersion, version);
                    if (Interlocked.Read(ref desiredVersion) != version)
                    {
                        wake.Set();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            Telemetry.RecordFailure();
            if (profileEvents)
            {
                BrokerEventSource.Log.HidWriteFailed(exception.GetType().FullName ?? exception.GetType().Name);
            }
            Volatile.Write(ref desiredForce, 0);
            Interlocked.Exchange(ref submittedVersion, Interlocked.Read(ref desiredVersion));
            while (barriers.TryDequeue(out PendingReport pending))
            {
                pending.Completion?.TrySetException(exception);
            }

            try
            {
                var stopAll = new byte[DfgtForceFeedbackReports.ReportLength];
                DfgtForceFeedbackReports.WriteStopAll(stopAll);
                Write(stopAll, clock.GetMicroseconds());
            }
            catch
            {
            }

            CloseTransport();
            faulted.Set();
        }
    }

    private void Write(ReadOnlyMemory<byte> report, long publishedAtMicroseconds)
    {
        long submittedAt = clock.GetMicroseconds();
        long publicationToSubmission = Math.Max(0, submittedAt - publishedAtMicroseconds);
        Telemetry.RecordSubmission(publicationToSubmission);
        transport.WriteOutputReportAsync(report, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        long completedAt = clock.GetMicroseconds();
        long publicationToCompletion = Math.Max(0, completedAt - publishedAtMicroseconds);
        long duration = Math.Max(0, completedAt - submittedAt);
        Telemetry.RecordCompletion(publicationToCompletion, duration);
        if (profileEvents)
        {
            BrokerEventSource.Log.HidWrite(publicationToSubmission, publicationToCompletion, duration);
        }
    }

    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref transportClosed, 1) == 0)
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void EnqueueBarrier(ReadOnlySpan<byte> report, TaskCompletionSource? completion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (report.Length != DfgtForceFeedbackReports.ReportLength)
        {
            throw new ArgumentException("DFGT reports must be exactly eight bytes.", nameof(report));
        }

        barriers.Enqueue(new PendingReport(report.ToArray(), clock.GetMicroseconds(), completion));
        Telemetry.RecordBarrierPublication(barriers.Count);
        wake.Set();
    }

    private readonly record struct PendingReport(
        byte[] Report,
        long PublishedAtMicroseconds,
        TaskCompletionSource? Completion);
}
