# Phase 2 runtime profiling

Status: provisional software evidence; no physical wheel was opened.

The authoritative broker targets 500 Hz (successive 2 ms QPC deadlines). It
uses a one-shot high-resolution waitable timer, MMCSS `Games` at critical task
priority, managed `Highest` priority, and a bounded 250 microsecond deadline
guard. HID submission is deliberately decoupled and completion-driven.

Run a repeatable idle baseline with:

```powershell
dotnet run --project .\src\LogiControl.Broker -c Release -- profile-runtime --seconds 60 --runs 10
```

Add `--stress` for CPU/GC pressure. This command creates 16 simultaneous software effects, applies
regular gain mutations, uses a null output sink, and never enumerates HID.

Aggregate histograms are always active. `--profile` enables managed EventSource
events in the live broker. `LOGICONTROL_FFB_PROFILE=1` enables native provider
callback and semantic-IPC TraceLogging. Physical profiling must report HID
submission and `WriteFile` completion separately from unobservable force
application.

## 2026-09-01 baseline

Reference machine: the current Windows development host. Ten idle runs of 60
measured seconds each completed with 16 effects:

- sustained approximately 500 Hz without cumulative drift;
- seven skipped periods across all ten runs and zero mixer overruns;
- zero mixer-thread allocated bytes in every run;
- absolute wake jitter p99 at or below 0.5 ms and p99.9 at or below 1 ms;
- mixer computation p99 in the 10 microsecond bucket;
- command-to-mix p99 in the 250 microsecond bucket.

A 60-second CPU/GC stress run produced 499.71 Hz, 20 skipped deadlines, zero
overruns, zero mixer allocations, wake jitter p99 at or below 1 ms and p99.9 at
or below 2 ms, mixer p99 in the 10 microsecond bucket, and command-to-mix p99 in
the 250 microsecond bucket. It recorded 561 gen-0 and 524 gen-1 collections.
Stress is reported as outlier evidence; the provisional idle p99 target is not
silently applied to it.

The subsequently started ten-minute duplicate soak was canceled. The sustained
idle and stress evidence above is sufficient for the software-only Phase 2
handoff; hardware-rate profiling remains part of deferred wheel acceptance.

Provisional idle goals remain:

- no cumulative drift at 500 Hz;
- mixer p99 at or below 0.25 ms with 16 effects;
- absolute wake jitter p99 at or below 0.5 ms and p99.9 at or below 2 ms;
- command-to-mix p99 at or below 2 ms;
- zero steady-state mixer allocations and no unbounded output backlog.
