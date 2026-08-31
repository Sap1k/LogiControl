# LogiControl implementation plan

Status: approved for implementation on 2026-08-30.

Vertical-slice progress on 2026-08-30: C294/REV_1326 discovery, automatic
switching, location-path C29A correlation, native broker attachment, 900-degree
initialization, development registration, and force-free x86/x64 DirectInput
enumeration have passed on the target DFGT. The bounded physical pulse, ETS2,
crash lease, and cold replug runs remain manual acceptance work. See
`docs/vertical-slice-runbook.md` and `docs/hardware-matrix.md`.

Hardware availability note: after the verified DFGT vertical-slice and
reconnect runs, further development proceeds without a physical wheel until
the maintainer explicitly says otherwise. Refactors should preserve captured
behavior through deterministic tests and recorded traces. Hardware-only
acceptance work is deferred and tracked for a later validation pass; it does
not block work that can be meaningfully verified without the device.

## Goal and constraints

LogiControl will restore Logitech-specific control and generic DirectInput
force feedback for classic wheels while using only Microsoft's standard HID
stack. It will not contain a custom kernel driver, require legacy Logitech
drivers, disable HVCI, or depend on a virtual controller.

The first hardware milestone is deliberately narrow:

1. Observe a physical Driving Force GT as `046D:C294`.
2. Read its device revision and identify the physical model as DFGT.
3. Send `F8 0A 00 00 00 00 00`, followed by
   `F8 09 03 01 00 00 00`, through `HidD_SetOutputReport`.
4. Handle detach/re-enumeration and correlate the resulting `046D:C29A`.
5. Attach it to the retained DFGT Control DirectInput/FFB path.
6. Validate generic DirectInput FFB in Euro Truck Simulator 2.
7. Validate 900-degree range, gain, autocenter, unplug/replug, and game-crash
   StopAll behavior.

## Target architecture

```text
Game
  -> DirectInput
  -> LogiControl.Ffb32 / LogiControl.Ffb64 (C++)
  -> local versioned IPC
  -> LogiControl.Broker (.NET, per-user)
  -> LogiControl.Protocol
  -> LogiControl.Hid / Microsoft HID APIs
  -> physical wheel
```

The C++ provider is temporarily allowed to retain effect state for the first
hardware proof. Afterward it becomes a semantic proxy; the broker becomes the
authoritative owner of effects, timing, mixing, gain, device sessions, and HID
output.

Physical wheel model, current USB presentation mode, and connected device
instance are separate concepts throughout the design.

## Research conclusions

### DFGT Control

The Windows-specific bridge is the most valuable reusable component. Its
provider implements `IDirectInputEffectDriver`, COM lifetime, effect parsing,
16 effect handles, envelopes, ramp/periodic/custom synthesis, condition
effects, global gain, and a 2 ms mixer. Its named-pipe transport sends rendered
constant/periodic values and condition parameters to a broker.

The retained Phase 1 implementation deliberately used an 8 ms output cadence
and never performs normal render IPC while holding the effect-state mutex.
Real-game testing showed that the original synchronous callback rendering could
stall a game's DirectInput thread; suppressing HID writes did not remove the
stall. DirectInput callbacks now only validate and update bounded in-memory
state. The mixer snapshots a render plan under the mutex and executes IPC after
releasing it.

The 8 ms cadence was a temporary debugging mitigation, not an observed DFGT
hardware limit. Phase 2 deletes that provider timer. Its authoritative managed
engine evaluates on successive 2 ms deadlines (500 Hz), while USB output is
completion-driven and coalesced independently of the mixer rate.

Provider attachment is also lazy. DirectInput enumeration and idle device
acquisition record the HID path but do not claim the legacy broker's single FFB
pipe. The pipe is opened only when an effect reaches its playing state. This
prevents launchers such as Steam from monopolizing the temporary broker before
the game process starts.

The broker owns HID output, profile/class gains, a 350 ms force lease, StopAll,
range control, and a dynamic range boundary. COM and OEMForceFeedback
registration exist for both x86 and x64.

The current implementation must not become the device abstraction: discovery,
registration, and HID validation hard-code `046D:C29A`; only one client/device
is supported; the provider—not the broker—owns authoritative DirectInput effect
state; and nonzero autocenter control is missing.

### new-lg4ff

`new-lg4ff/hid-lg4ff.c` is the behavioral reference for:

- physical-model identification from current mode and `bcdDevice`;
- supported alternate modes and exact switch sequences;
- one software-mixed constant slot plus three hardware condition slots;
- effect timing, envelope, iteration, and condition behavior;
- range strategies, autocenter, class gain, friction capability, and quirks.

Initial classic-wheel identity rules are:

| Physical model | Revision test | Native PID | Native switch from C294 |
| --- | --- | --- | --- |
| DFP | `(version & F000) == 1000` | `C298` | `F8 01 00 00 00 00 00` |
| G25 | `(version & FF00) == 1200` | `C299` | `F8 10 00 00 00 00 00` |
| DFGT | `(version & FF00) == 1300` | `C29A` | `F8 0A...`, then `F8 09 03 01 00 00 00` |
| G27 | `(version & FFF0) == 1230` | `C29B` | `F8 0A...`, then `F8 09 04 01 00 00 00` |

Unknown `C294` revisions are not switched.

### Other references

- OpenG27 supplies useful C# transport, serialized-write, lifecycle, and
  safety-test patterns, but it does not identify C294 by revision and its game
  FFB path is telemetry-specific.
- WheelEmulator demonstrates event-driven hotplug and Windows
  `HidD_SetOutputReport`, but its generic-Windows-FFB claim and effect encoders
  are not accepted as authoritative.
- `lg4ff_userspace` demonstrates transport separation but is an incomplete
  Linux/uinput port with PID-only discovery and a simplified mode switch.
- Other `IDirectInputEffectDriver` projects confirm the ABI and registry model,
  but usually keep the mixer inside the DLL.

## Core interfaces

Managed protocol/device types:

- `WheelModel`
- `UsbPresentationMode`
- `WheelDefinition`
- `HidDeviceSnapshot`
- `WheelIdentity`
- `IWheelProtocol`
- `IHidTransport`
- `WheelSessionManager`

The HID transport exposes control-transfer `SetOutputReport` separately from
continuous `WriteOutputReport`; callers cannot silently substitute one for the
other.

The long-term semantic IPC is bounded, explicitly serialized little-endian
binary. It has a versioned header containing magic, major/minor version, message
type, request ID, session ID, and payload length. Messages cover:

- hello/bind/unbind/heartbeat/close session;
- validate or upsert, start, stop, destroy, and query effect;
- set gain, send DirectInput device command, and query device state.

The broker allocates effect handles. `DIEP_NODOWNLOAD` validates without
mutation. Pointer-owned `DIEFFECT` data is deep-copied into a discriminated
`EffectDefinition`; native pointer layouts never cross IPC.

## Phase 0: bootstrap and baseline

### Work

- Establish GPL-3.0-or-later licensing, contributor instructions, provenance,
  architecture, protocol sources, and physical safety policy.
- Add .NET 10 solution/build settings and CMake x86/x64 presets.
- Import and minimally rename DFGT Control's provider, legacy broker, IPC,
  installer logic, and smoke tests with MIT notices.
- Preserve the existing provider/broker wire contract for Phase 1.
- Add managed/native CI without unattended hardware output.

### Risks and tests

Renaming COM, pipes, and registry identities can break the baseline. Before
hardware changes, test report vectors, DLL exports, class factory creation,
`IID_IDirectInputEffectDriver`, legacy IPC layout, and installer dry-runs.

### Done

A clean checkout builds both provider architectures, the legacy broker, managed
projects, tests, and a development package without requiring hardware.

## Phase 1: DFGT C294 to C29A and existing FFB

### Work

- Enumerate HID interfaces using SetupAPI, `HidD_GetAttributes`, and
  `HidP_GetCaps`; collect container/location evidence.
- Use `CM_Register_Notification`, register before initial enumeration, and
  rescan/debounce on a worker thread.
- Identify DFGT from C294 plus the `13xx` revision matcher.
- Send the two mode reports through `HidD_SetOutputReport`, close the stale
  handle, and wait up to 10 seconds for matching C29A.
- Before switching modes, open C294 for read-only input without sending output.
  The firmware performs most of its power-on sweep before the HID collection
  exposes useful samples, so require observable steering movement followed by
  350 ms settled near C294's 10-bit center. Only then send the native-mode reports.
  Timeout after 15 seconds and fail without output. After correlated C29A
  arrival, allow a short settle delay before StopAll, range, and profile output.
- Register OEMForceFeedback only under native C29A and register both COM
  bitnesses.
- Retain the current DFGT Control provider/broker path and safety lease.
- Bound provider output to an 8 ms cadence and keep blocking pipe/HID work off
  game-facing DirectInput callback locks.
- Add broker autocenter strength: `F5` disables; `FE 0D ...` sets strength;
  `14` enables. Default is zero and active game effects disable firmware
  autocenter to avoid doubled force.
- Emit structured device, switch, IPC, report, and fail-safe diagnostics.

### Risks and tests

Risks include selecting the correct top-level collection, legacy filter
conflicts, unstable container identity, shared-handle behavior, DirectInput
capability caching, and games that do not reacquire after re-enumeration.

Tests cover revision boundaries, unknown-C294 no-write behavior, duplicate
arrival, removal during switch, timeout, already-native startup, rapid
unplug/replug, exact SetOutputReport calls, x86/x64 provider cycles, low-force
direction, range, gain, autocenter, and forced game termination.

### Done

A cold-connected DFGT switches from C294 to C29A, receives normal ETS2
DirectInput FFB, supports the control tests, and safely recovers from game
termination and unplug/replug.

## Phase 2: thin provider and authoritative .NET broker

Implementation status on 2026-09-01: implemented, automated-test-only. The
semantic protocol, complete effect engine, 500 Hz scheduler, asynchronous HID
output pump, x86/x64 thin provider, and broker-owned DFGT lifecycle are present.
The legacy targets, device agent, and legacy wire protocol have been deleted.
Git history is the only fallback before the next physical-wheel replay.

### Work

- Use a fixed 32-byte little-endian `LCFF` header and a maximum 64 KiB frame.
  Keep provider requests ordered, current-user-only, bounded, and free of
  pointers/native layouts. Connect on the first effect operation, heartbeat at
  100 ms, and destroy the session after a 350 ms owner-lease expiry.
- Replace the provider's effect table/mixer with semantic IPC. The provider
  keeps only COM/DirectInput ABI translation, selected-field deep marshalling,
  validation, result mapping, and IPC.
- Move effect state, fake-clock-capable timing, mixer, slot allocation, gains,
  and HID encoding into `LogiControl.Broker`.
- Mix constant, ramp, square, sine, triangle, sawtooth-up/down, and bounded
  one-channel custom effects into firmware slot 0. Implement DirectInput
  duration, delay, sample period, phase, iterations, envelope, direction,
  per-effect gain, game gain, class gain, and profile master gain semantics.
- Treat condition-slot allocation as a runtime reservation problem: active or
  delayed spring/damper/friction/inertia effects reserve the lowest free slot
  from 1 through 3. Downloads consume no slot. Pause retains reservations;
  stop, destroy, natural completion, reset, session loss, or removal releases
  them. A fourth condition returns `DIERR_DEVICEFULL`. DFGT friction is native;
  inertia maps to damper.
- Run the mixer on successive 2 ms QPC deadlines using a one-shot high-resolution
  waitable timer, MMCSS `Games`/critical priority, `Highest` managed priority,
  and a 250 microsecond deadline guard. Skip missed periods rather than burst
  catch-up. Sleep without a timer when no effect needs evaluation.
- Keep USB writes asynchronous and completion-driven with one write in flight.
  Coalesce superseded slot-0 values while preserving FIFO barriers for StopAll,
  range, autocenter, and condition stop/start/update.
- Move discovery, calibration gating, `HidD_SetOutputReport` mode switching,
  re-enumeration correlation, native attach, removal, recovery, range, and
  autocenter policy into the managed broker.
- Delete the legacy broker, legacy wire types, device agent, scripts, and CMake
  targets after deterministic/cross-bitness/safety checks and sustained 500 Hz
  software profiling. Completed 2026-09-01; no physical wheel was opened.

### Risks and tests

Use xUnit v3 for all managed unit/integration tests. Test deep serialization,
malformed frames, every effect shape and lifecycle,
gain order, conditions, slot exhaustion, pause/reset/actuators, IPC loss,
provider crash, broker restart, timing jitter, and replay of Phase 1 traces.

A fourth concurrent hardware condition returns `DIERR_DEVICEFULL`; it does not
silently replace another effect.

Profiling uses QPC-correlated native TraceLogging and managed EventSource plus
fixed-bucket aggregate histograms. Normal operation records aggregates only;
`--profile`/`LOGICONTROL_FFB_PROFILE=1` enables per-event traces. The
`profile-runtime` broker command runs a 16-effect, no-HID benchmark and supports
repeat counts and CPU/GC stress. The completed evidence is recorded in
`docs/phase2-profiling.md`; further soak testing is deferred until it informs a
specific regression or the physical acceptance session.

### Done

The C++ DLL contains only COM/ABI translation, validation, deep serialization,
and IPC. Pipe loss destroys broker-owned effect state and issues StopAll. The
.NET broker passes the complete Phase 1 matrix.

## Phase 3: generalized classic-wheel support

### Work

- Add typed definitions in order: G27, G25, DFP.
- Generate native-PID registration for C29A, C29B, C299, and C298.
- Add EXT_CMD9, EXT_CMD16, and EXT_CMD1 switching strategies.
- Add `F8 81` range for DFGT/G25/G27 and DFP coarse-plus-fine range.
- Support multiple independent physical device sessions while allowing one
  active DirectInput owner per wheel.
- Defer G29/G923, LEDs, pedal remapping, and arbitrary compatibility-mode
  selection.

### Tests and done

Use table-driven identity/command/registration tests, fake two-wheel tests, and
the common physical certification checklist. A model remains experimental until
recorded hardware results pass the matrix.

## Phase 4: profiles, UI, packaging, and polish

- Add an optional English-first .NET 10 WPF client that never writes HID.
- Add atomic, versioned JSON profiles keyed by exact normalized executable path.
- Configure range, master/class gains, idle autocenter, and optional soft stop.
- Export privacy-conscious identity, driver-binding, HVCI, state, report, and
  mixer diagnostics.
- Replace development scripts with a backup-first MSI/WiX installer for both
  COM architectures, native PID registration, per-user broker startup, upgrade,
  and manifest-owned uninstall.
- Keep Logitech driver-store cleanup separate and explicitly elevated.

## Hardware assumptions still requiring proof

- The target DFGT reports `VersionNumber` in the `13xx` family in both modes.
- Its C294 output collection reports at least eight bytes and accepts both
  commands through `HidD_SetOutputReport`.
- The second command returns cleanly before detach invalidates the handle.
- Container or location evidence correlates C294 with C29A.
- Shared HID access permits game input and broker output concurrently.
- Native HID exposes the expected DFGT axes and buttons.
- Clean Windows recognizes C29A OEMForceFeedback registration for both game
  bitnesses.
- `F8 81` range and the autocenter encoding behave as expected physically.
- ETS2 reaches the provider without Logitech software.
- The 350 ms lease avoids both sustained force and false stops.
- Broker recovery cannot guarantee that every running game reacquires a wheel
  after USB re-enumeration.

## Initial task order

1. Bootstrap repository, licensing, build, CI, and source documentation.
2. Import the DFGT Control provider/legacy broker with notices.
3. Prove x86/x64 COM loading and legacy IPC with a fake broker.
4. Build the read-only HID diagnostic enumerator.
5. Add DFGT identity and command-vector tests.
6. Implement notification-driven mode switching and re-enumeration matching.
7. Attach C29A to the legacy broker and add autocenter/diagnostics.
8. Install native-PID DirectInput registration and run provider diagnostics.
9. Validate physical safety, range, gain, autocenter, crash, and hotplug.
10. Complete the ETS2 run and preserve its trace as the Phase 2 baseline.
