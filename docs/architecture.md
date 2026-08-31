# Architecture

## Trust and process boundaries

```text
Untrusted game process
  -> in-process x86/x64 DirectInput provider
  -> current-user-only named pipe
  -> per-user LogiControl broker
  -> validated HID reports
  -> inbox Microsoft HID stack
```

Games control effect inputs but never receive a HID handle. The broker is the
only long-lived HID writer. The UI is a separate, optional control client and
never communicates with hardware directly.

## Phase boundaries

Phase 1 retained a temporary native legacy broker and provider-side mixer to
reduce uncertainty during the first physical DFGT proof. A managed device agent
owns discovery, identification, mode switching, and legacy-broker supervision.

Phase 2 moves device lifecycle and all FFB state into `LogiControl.Broker` and
changes the provider pipe to bounded semantic effect operations. The new path
is implemented and automated-test-only. The legacy projects and wire protocol
were deleted on 2026-09-01 after deterministic, x86/x64, safety, and sustained
500 Hz software validation. Git history is the only fallback before physical
replay.

## Phase 2 runtime ownership

The provider has no effect table or timer. The broker's single mixer thread is
the only mutator of session, playback, and slot state. It evaluates active
effects every 2 ms and wakes immediately for commands. Slot 0 is a coalesced
software mix. Slots 1–3 are lowest-free reservations for condition effects and
are written only on start, update, resume, and stop.

The HID writer owns one overlapped write at a time. Desired slot-0 state may be
superseded while a write is pending; StopAll, range, autocenter, and condition
transitions are non-droppable FIFO barriers. Mode switching remains exclusively
on `HidD_SetOutputReport`; continuous native output uses overlapped `WriteFile`.

The broker owns arrival, revision-gated identification, calibration, switching,
correlation, attach, removal, and recovery. It does not advertise the device as
ready until StopAll, autocenter disable, range/profile output, and an overlapped
output completion have succeeded.

## Identity model

`WheelModel`, `UsbPresentationMode`, and a connected HID interface are distinct.
A compatibility PID can be shared by several physical models. Identification
therefore combines VID/PID, revision matcher, and connection evidence.

## Failure rules

- Provider disconnect: destroy its session and send StopAll.
- Force heartbeat expiry: send StopAll and mark the session failed.
- Device removal: cancel I/O, close handles, clear effects, and await arrival.
- HID write failure: clear desired output, attempt StopAll, detach the failed
  pump, invalidate every handle, and keep the broker runtime available for a
  later device reattach.
- Invalid IPC or effect data: reject before any HID write.
- Broker restart: start disconnected, discover afresh, and send StopAll before
  accepting force.
- Unknown model/revision: diagnostics only; never switch or generate force.
