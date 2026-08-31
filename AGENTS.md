# LogiControl contributor instructions

## Project purpose

LogiControl is a Windows-only, user-mode replacement for the legacy Logitech
Gaming Software / WingMan force-feedback stack for classic Logitech wheels.
It uses Microsoft's inbox HID stack for input and device access.

These constraints are absolute:

- Do not add, port, install, or require a custom kernel driver.
- Do not require disabling Memory Integrity/HVCI, Secure Boot, or another
  Windows security feature.
- Do not introduce a virtual controller unless the project scope is explicitly
  changed in a reviewed design document.
- Keep Logitech-specific control and force-feedback behavior in userspace.

## Intended architecture

```text
Game
  -> DirectInput
  -> LogiControl.Ffb (x86/x64 C++ COM provider)
  -> versioned local IPC
  -> LogiControl.Broker (.NET, per-user process)
  -> LogiControl.Protocol
  -> LogiControl.Hid / Microsoft hidclass.sys
  -> physical wheel
```

The native provider should eventually contain only COM/DirectInput ABI
translation, validation/deep serialization, and IPC. The broker is the long-term
owner of effect state, mixing, time, profiles, safety policy, and HID output.

Phase 1 deliberately retains the imported DFGT Control effect path until a
physical DFGT has completed the C294-to-C29A and real-game acceptance flow. Do
not combine that proof with the Phase 2 mixer move unless the implementation
plan is explicitly revised.

## Device identity and mode switching

- A USB PID describes the wheel's current presentation mode, not necessarily
  its physical model.
- Never identify `VID_046D&PID_C294` as a particular wheel without checking the
  `HIDD_ATTRIBUTES.VersionNumber`/USB `bcdDevice` matcher.
- Unknown C294 revisions are read-only: log them and send no mode command.
- Mode switches use `HidD_SetOutputReport` with the exact report length from
  `HIDP_CAPS.OutputReportByteLength`. Do not silently replace this with
  `WriteFile`.
- Treat detach/re-enumeration as an asynchronous state transition. Close stale
  handles and correlate the resulting native device by connection evidence.

## Physical safety

- Automated tests must never generate physical force.
- Hardware tests must be explicitly selected, interactive, and low-force by
  default. They must identify the target device before enabling output.
- Every HID-output owner must have `StopAll` paths for normal shutdown, IPC
  loss, client crash, invalid input, device removal, and internal failure.
- Clamp all game-controlled magnitudes, coefficients, gains, ranges, durations,
  sample counts, and payload lengths at the trust boundary.
- Do not weaken named-pipe ACLs or allow remote clients.

## Current hardware availability

Until the project maintainer explicitly says otherwise, no physical wheel is
available for further development or validation.

- Continue implementation against unit tests, fake HID/IPC transports, golden
  Logitech report vectors, and the recorded DFGT traces in
  `docs/hardware-matrix.md`.
- Preserve the behavior of the verified DFGT vertical slice while refactoring;
  encode captured behavior as regression tests before replacing it.
- Do not attempt to enumerate, open, switch, or generate force on physical
  hardware, and do not wait for a wheel to be connected.
- Clearly label new or changed hardware behavior as automated-test-only and add
  it to the deferred hardware-validation list. Do not promote it to physically
  verified until the maintainer supplies a new run.
- Lack of hardware is not a blocker for architecture work that can be validated
  deterministically. Small hardware-specific corrections may be made in a
  later validation pass when the wheel becomes available again.

## Repository boundaries

- `src/LogiControl.Protocol`: OS/UI-independent wheel definitions and encoders.
- `src/LogiControl.Hid`: Windows HID, SetupAPI, and ConfigMgr interop.
- `src/LogiControl.DeviceAgent`: temporary Phase 1 device lifecycle supervisor.
- `src/LogiControl.Broker`: authoritative Phase 2 runtime.
- `src/LogiControl.UI`: optional WPF broker client; it never writes HID.
- `native/LogiControl.Ffb`: x86/x64 DirectInput COM adapter.
- `native/LogiControl.LegacyBroker`: temporary DFGT baseline removed in Phase 2.
- `tests`: pure unit/integration tests; physical tests live under explicit tools.

Keep protocol code deterministic and testable with fake time and fake
transports. Keep Windows handles behind safe-handle or disposable owners. Avoid
placing device/model branches in the DirectInput DLL.

## Build and verification

Managed code targets .NET 10 and treats warnings as errors.

```powershell
dotnet build .\LogiControl.slnx -c Release
dotnet run --project .\tests\LogiControl.Protocol.Tests -c Release
```

After the Visual Studio C++ workload and CMake are installed:

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64-release
cmake --preset windows-x86
cmake --build --preset windows-x86-release
```

Do not mark hardware behavior as supported based only on unit tests or another
project's README. Record physical results in `docs/hardware-matrix.md`.

## Licensing and provenance

The project is licensed GPL-3.0-or-later. Add SPDX identifiers to new source
files.

- Preserve MIT notices for directly reused DFGT Control, OpenG27,
  WheelEmulator, or other permissively licensed code.
- `new-lg4ff/hid-lg4ff.c` is GPL-2.0-or-later. Prefer an independently expressed
  implementation from protocol behavior and retain attribution when code is
  derivative.
- Treat `lg4ff_userspace` as conceptual reference only unless its project-wide
  license grant is clarified. Do not copy it into this tree.
- Update `THIRD_PARTY_NOTICES.md` and `docs/protocol-sources.md` in the same
  change that introduces borrowed code or protocol behavior.

## Change expectations

- Add or update golden report vectors for every protocol encoder change.
- Add state-machine tests for arrival, duplicate notifications, timeout,
  removal, and recovery.
- Keep IPC explicitly versioned and bounded; never transmit native pointer
  layouts such as `DIEFFECT` directly.
- Installation changes must be backup-first and uninstall only manifest-owned
  files and registry entries.
- Update the implementation plan when a phase boundary or major architectural
  decision changes.
