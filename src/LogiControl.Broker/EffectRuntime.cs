// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.InteropServices;
using LogiControl.Protocol;
using Microsoft.Win32.SafeHandles;

namespace LogiControl.Broker;

public sealed partial class EffectRuntime : IDisposable
{
    public const int FrequencyHertz = 500;
    public const int PeriodMicroseconds = 1_000_000 / FrequencyHertz;
    public const int DeadlineGuardMicroseconds = 250;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint AvrtPriorityCritical = 2;

    private readonly IRuntimeMixer mixer;
    private readonly IMonotonicClock clock;
    private readonly IForceFeedbackOutputSink output;
    private readonly ConcurrentQueue<RuntimeRequest> commands = new();
    private readonly AutoResetEvent commandReady = new(false);
    private readonly ManualResetEvent shutdown = new(false);
    private readonly Thread thread;
    private readonly RuntimeTelemetry telemetry = new();
    private readonly bool profileEvents;
    private bool hadActiveEffects;
    private int appliedRange = int.MinValue;
    private int appliedIdleAutocenter = int.MinValue;
    private bool disposed;

    public EffectRuntime(IRuntimeMixer mixer, IMonotonicClock clock, IForceFeedbackOutputSink output,
        bool profileEvents = false)
    {
        this.mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.profileEvents = profileEvents;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LogiControl 500 Hz effect engine",
            Priority = ThreadPriority.Highest,
        };
    }

    public RuntimeTelemetry Telemetry => telemetry;

    public OutputTelemetry? OutputTelemetry => output switch
    {
        CoalescingHidOutputPump pump => pump.Telemetry,
        SwitchableForceFeedbackOutputSink switchable => switchable.Telemetry,
        _ => null,
    };

    public void Start() => thread.Start();

    internal void ResetOutputPolicyForAttach()
    {
        appliedRange = int.MinValue;
        appliedIdleAutocenter = int.MinValue;
        hadActiveEffects = false;
    }

    public T Invoke<T>(Func<T> command, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var request = new RuntimeRequest<T>(command, clock.GetMicroseconds());
        commands.Enqueue(request);
        commandReady.Set();
        if (!request.Completed.Wait(timeout))
        {
            throw new TimeoutException("The effect runtime did not acknowledge the command.");
        }

        request.ThrowIfFailed();
        return request.Result;
    }

    public void Invoke(Action command, TimeSpan timeout) =>
        Invoke(() =>
        {
            command();
            return true;
        }, timeout);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Set();
        commandReady.Set();
        if (thread.IsAlive)
        {
            thread.Join();
        }

        Span<byte> stopAll = stackalloc byte[Protocol.DfgtForceFeedbackReports.ReportLength];
        Protocol.DfgtForceFeedbackReports.WriteStopAll(stopAll);
        output.PublishBarrier(stopAll);
        commandReady.Dispose();
        shutdown.Dispose();
        output.Dispose();
    }

    private void Run()
    {
        GCLatencyMode previousLatency = GCSettings.LatencyMode;
        nint mmcss = 0;
        uint taskIndex = 0;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            mmcss = AvSetMmThreadCharacteristics("Games", ref taskIndex);
            if (mmcss != 0)
            {
                _ = AvSetMmThreadPriority(mmcss, AvrtPriorityCritical);
            }

            using EventWaitHandle timer = CreateHighResolutionTimer();
            var waits = new WaitHandle[] { timer, commandReady, output.Faulted, shutdown };
            long deadline = clock.GetMicroseconds() + PeriodMicroseconds;
            bool armed = false;

            while (true)
            {
                if (mixer.HasActiveEffects && !armed)
                {
                    Arm(timer, Math.Max(1, deadline - clock.GetMicroseconds() - DeadlineGuardMicroseconds));
                    armed = true;
                }

                int signaled = WaitHandle.WaitAny(waits);
                if (signaled == 3)
                {
                    mixer.StopAll();
                    _ = RenderAndPublish();
                    return;
                }


                if (signaled == 2)
                {
                    mixer.StopAll();
                    _ = RenderAndPublish();
                    deadline = clock.GetMicroseconds() + PeriodMicroseconds;
                    armed = false;
                    continue;
                }

                if (signaled == 1)
                {
                    RuntimeRequest? first = null;
                    RuntimeRequest? last = null;
                    while (commands.TryDequeue(out RuntimeRequest? request))
                    {
                        request.Execute();
                        if (first is null)
                        {
                            first = request;
                        }
                        else
                        {
                            last!.NextBatch = request;
                        }

                        last = request;
                    }

                    _ = RenderAndPublish();
                    long mixedAt = clock.GetMicroseconds();
                    while (first is not null)
                    {
                        long latency = Math.Max(0, mixedAt - first.EnqueuedAtMicroseconds);
                        telemetry.RecordCommand(latency);
                        if (profileEvents)
                        {
                            BrokerEventSource.Log.CommandApplied(latency);
                        }

                        RuntimeRequest? next = first.NextBatch;
                        first.NextBatch = null;
                        first = next;
                    }

                    if (!mixer.HasActiveEffects)
                    {
                        deadline = clock.GetMicroseconds() + PeriodMicroseconds;
                        armed = false;
                    }
                    else if (!armed)
                    {
                        deadline = clock.GetMicroseconds() + PeriodMicroseconds;
                    }

                    continue;
                }

                armed = false;
                long woke = clock.GetMicroseconds();
                while (woke < deadline)
                {
                    Thread.SpinWait(16);
                    woke = clock.GetMicroseconds();
                }

                long jitter = woke - deadline;
                long missed = 0;
                long computationStart = woke;
                long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                MixerSnapshot snapshot = RenderAndPublish();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                long computation = clock.GetMicroseconds() - computationStart;
                deadline += PeriodMicroseconds;
                long now = clock.GetMicroseconds();
                while (deadline <= now)
                {
                    deadline += PeriodMicroseconds;
                    missed++;
                }

                telemetry.RecordTick(jitter, computation, missed, allocated);
                if (profileEvents)
                {
                    BrokerEventSource.Log.MixerTick(deadline - PeriodMicroseconds, woke, computation, missed,
                        snapshot.ActiveEffectCount);
                }
            }
        }
        finally
        {
            while (commands.TryDequeue(out RuntimeRequest? request))
            {
                request.Fail(new InvalidOperationException("The effect runtime stopped."));
            }

            if (mmcss != 0)
            {
                _ = AvRevertMmThreadCharacteristics(mmcss);
            }

            GCSettings.LatencyMode = previousLatency;
        }
    }

    private MixerSnapshot RenderAndPublish()
    {
        MixerSnapshot snapshot = mixer.Render();
        if (mixer.TryConsumeStopAllBarrier())
        {
            Span<byte> stopAll = stackalloc byte[DfgtForceFeedbackReports.ReportLength];
            DfgtForceFeedbackReports.WriteStopAll(stopAll);
            output.PublishBarrier(stopAll);
        }

        RuntimeSettings settings = mixer.RuntimeSettings;
        if (settings.RangeDegrees != appliedRange)
        {
            Span<byte> range = stackalloc byte[Protocol.DfgtForceFeedbackReports.ReportLength];
            Protocol.DfgtForceFeedbackReports.WriteRange(range, settings.RangeDegrees);
            output.PublishBarrier(range);
            appliedRange = settings.RangeDegrees;
        }

        bool active = snapshot.ActiveEffectCount > 0;
        if (active && !hadActiveEffects)
        {
            Span<byte> disable = stackalloc byte[Protocol.DfgtForceFeedbackReports.ReportLength];
            Protocol.DfgtForceFeedbackReports.WriteDisableAutocenter(disable);
            output.PublishBarrier(disable);
        }

        output.PublishSoftwareForce(snapshot.SoftwareForce);
        while (mixer.TryDequeueConditionChange(out ConditionSlotChange change))
        {
            output.PublishConditionChange(change);
        }

        if (!active && (hadActiveEffects || settings.IdleAutocenter != appliedIdleAutocenter))
        {
            if (hadActiveEffects)
            {
                Span<byte> stop = stackalloc byte[Protocol.DfgtForceFeedbackReports.ReportLength];
                Protocol.DfgtForceFeedbackReports.WriteSlotStop(stop, 0);
                output.PublishBarrier(stop);
            }

            Span<byte> autocenter = stackalloc byte[Protocol.DfgtForceFeedbackReports.ReportLength];
            Protocol.DfgtForceFeedbackReports.WriteAutocenterParameters(autocenter, settings.IdleAutocenter);
            output.PublishBarrier(autocenter);
            if (settings.IdleAutocenter > 0)
            {
                Protocol.DfgtForceFeedbackReports.WriteEnableAutocenter(autocenter);
            }
            else
            {
                Protocol.DfgtForceFeedbackReports.WriteDisableAutocenter(autocenter);
            }

            output.PublishBarrier(autocenter);
            appliedIdleAutocenter = settings.IdleAutocenter;
        }

        hadActiveEffects = active;

        return snapshot;
    }

    private static EventWaitHandle CreateHighResolutionTimer()
    {
        SafeWaitHandle handle = CreateWaitableTimerEx(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"CreateWaitableTimerEx failed with {Marshal.GetLastPInvokeError()}.");
        }

        var timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        timer.SafeWaitHandle = handle;
        return timer;
    }

    private static void Arm(EventWaitHandle timer, long dueMicroseconds)
    {
        long due100Nanoseconds = checked(-dueMicroseconds * 10);
        if (!SetWaitableTimer(timer.SafeWaitHandle, in due100Nanoseconds, 0, 0, 0, false))
        {
            throw new InvalidOperationException($"SetWaitableTimer failed with {Marshal.GetLastPInvokeError()}.");
        }
    }

    private abstract class RuntimeRequest
    {
        protected RuntimeRequest(long enqueuedAtMicroseconds)
        {
            EnqueuedAtMicroseconds = enqueuedAtMicroseconds;
        }

        public long EnqueuedAtMicroseconds { get; }

        public RuntimeRequest? NextBatch { get; set; }

        public abstract void Execute();

        public abstract void Fail(Exception exception);
    }

    private sealed class RuntimeRequest<T> : RuntimeRequest
    {
        private readonly Func<T> command;
        private Exception? failure;

        public RuntimeRequest(Func<T> command, long enqueuedAtMicroseconds)
            : base(enqueuedAtMicroseconds)
        {
            this.command = command;
        }

        public ManualResetEventSlim Completed { get; } = new(false);

        public T Result { get; private set; } = default!;

        public override void Execute()
        {
            try
            {
                Result = command();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Completed.Set();
            }
        }

        public override void Fail(Exception exception)
        {
            failure = exception;
            Completed.Set();
        }

        public void ThrowIfFailed()
        {
            if (failure is not null)
            {
                throw new InvalidOperationException("The runtime command failed.", failure);
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateWaitableTimerEx(nint timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle timer,
        in long dueTime,
        int periodMilliseconds,
        nint completionRoutine,
        nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvSetMmThreadPriority(nint avrtHandle, uint priority);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint avrtHandle);
}
