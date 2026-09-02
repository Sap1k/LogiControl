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

## Runtime and selected-device ownership

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

Phase 3 keeps that runtime singular. Discovery may retain several read-only
candidates with broker-assigned IDs, but only the selected candidate may be
calibrated, mode-switched, opened for output, bound by a provider, or initialized.
One candidate is selected automatically; two or more require an explicit
selection. An explicit selection is sticky across removal. Changing it issues
StopAll, invalidates effects and existing path bindings, closes the old output
owner, and only then initializes the replacement.

Candidates represent physical wheels, not transient HID paths. A broker-lifetime
registry accumulates container IDs and location paths, with parent and instance
IDs used only when comparable stronger evidence is absent. Presentations with
the same model/revision and correlating evidence are grouped; the unique native
presentation wins. Multiple equally writable endpoints or correlations to more
than one registry record are ambiguous and therefore read-only. The candidate
limit is applied after grouping.

Selection owns a monotonically increasing revision and cancellation lease.
Changing selection clears readiness and cancels the old calibration, switch,
poll, settle, open, initialization, or ready-barrier work before waiting for the
serialized lifecycle gate. The revision is checked before every HID open/write
and before readiness publication. Supersession cleans up with StopAll and does
not manufacture a HID fault state.

Protocol behavior is selected through catalog strategies (mode switch, range,
autocenter, friction capability, and HID report layout), never by branching on
the physical model in the native provider or effect runtime.
Protocol construction requires a complete `WheelDefinition`, including its
supported steering ranges; a profile alone is not sufficient. Catalog strategy
and capability abstractions remain extension points for additional wheels.

Runtime settings belong to the selected wheel, not individual game sessions.
The device manager validates model-specific constraints before applying them
through the coordinator's global settings path to existing and future sessions.

All catalog wheels use direct constant command `0x00`; the force byte occupies
the field for its selected firmware slot. Slot 0 remains the software mix and
slots 1–3 remain native conditions. DFGT/G25/G27 ranges are continuous from
40°–900°; DFP exposes only 200° and 900° until input interposition exists.
Condition response gain (direction, DirectInput effect, game, and master) scales
coefficient slope and saturation, while spring/damper/friction class gain scales
saturation only.

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
