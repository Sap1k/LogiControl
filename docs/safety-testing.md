# Physical hardware safety

## Automated test boundary

Normal builds and CI may use fake HID transports, recorded input, golden output
vectors, fake clocks, and fake IPC. They must not enumerate a real device for
write access or send an output report.

Phase 2 lifecycle tests inject `IHidDeviceEnumerator`, notification,
calibration, transport-factory, and transport fakes. The broker's
`--fake-hid` mode explicitly bypasses physical enumeration and is the only mode
used by automated x86/x64 COM playback tests. The `profile-runtime` command
uses a null output sink and never enumerates HID.

## Interactive hardware tools

Any future tool capable of force must:

1. Require an explicit hardware flag and interactive confirmation.
2. Display VID, PID, revision, product name, and connection identity.
3. Refuse unknown revisions and ambiguous multiple-device selections.
4. Begin by sending StopAll and disabling autocenter.
5. Default to a short, low-magnitude pulse with a documented upper bound.
6. Maintain a heartbeat/lease and StopAll on timeout, Ctrl+C, exception, IPC
   loss, shutdown, and device removal.
7. Never run from CI.

The production broker may manage hardware only through its revision-gated
device manager. It marks semantic starts ready only after StopAll, autocenter
disable, range/profile initialization, and a completed output barrier. Removal,
write fault, broker shutdown, provider loss, and lease expiry all invalidate
force ownership and attempt StopAll before closing a valid transport.

Mode switching is non-force output but follows the same exact-device and
unknown-revision refusal rules.

## Manual release evidence

Physical results belong in `docs/hardware-matrix.md`. A README claim requires a
repeatable result on the named hardware, not inference from a related PID or
another operating system.
