# Hardware support matrix

This table records evidence; it is not a roadmap checkbox.

## Hardware availability

As of 2026-08-30, the physical DFGT is no longer available for ongoing
development. Until the maintainer explicitly announces otherwise, development
continues using automated tests, fake transports, golden vectors, and the
captured traces below. Existing physical results remain valid evidence, but any
subsequent behavioral change is considered automated-test-only until it is
replayed on hardware. Deferred checks should be accumulated for the next
hardware-validation session rather than blocking testable implementation work.

Phase 2 changed the provider, broker, mixer cadence, report ownership,
condition-slot allocation, asynchronous output, autocenter, and lifecycle.
Those changes are **automated-test-only** as of 2026-09-01 even where the
corresponding Phase 1 behavior was physically verified. The next wheel session
tests only the new path; no Phase 2 behavior is promoted from this matrix based
on fakes or profiling alone.

| Physical wheel | Compatibility identity | Native PID | Mode switch | Input | Range | Autocenter | DirectInput FFB x86 | DirectInput FFB x64 | Game/crash/hotplug | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Driving Force GT | C294 / REV_1326 observed | C29A | Passed repeatedly; location-path correlated | 3 axes / 21 buttons / 1 POV | 900° passed at startup and after repeated reconnect | Disabled only | Enumeration passed | Constant, sine, spring, damper, and The Bus passed | The Bus performance/FFB plus calibrated reconnect and range restoration passed; crash lease pending | Experimental |
| G27 | No test hardware recorded | C29B | Not implemented | Not tested | Not tested | Not tested | Not tested | Not tested | Not tested | Planned |
| G25 | No test hardware recorded | C299 | Not implemented | Not tested | Not tested | Not tested | Not tested | Not tested | Not tested | Planned |
| Driving Force Pro | No test hardware recorded | C298 | Not implemented | Not tested | Not tested | Not tested | Not tested | Not tested | Not tested | Planned |

For every physical run, record Windows build, HVCI state, driver binding,
`VersionNumber`, HID report sizes, connection evidence, LogiControl commit,
test tool/game, and whether the result was repeatable after a cold reconnect.

## DFGT run: 2026-08-30

- Windows 11 build family 26200; VBS status `2`, HVCI service running.
- Driver binding before and after: Microsoft `HidUsb`, `input.inf`.
- C294: revision `1326`, usage `0001:0004`, reports input/output/feature
  `8/8/9`, container `4e5bbf91-a451-11f1-b40a-f068e3c9787d`.
- C29A: revision `1326`, usage `0001:0004`, reports `9/8/132`, container
  `1e9b4efe-a4ae-11f1-b40b-f068e3c9787d`.
- The container ID changed across re-enumeration; the USB location path
  `PCIROOT(0)#PCI(0803)#PCI(0000)#USBROOT(0)#USB(2)` remained stable and was
  used for correlation.
- Both mode reports succeeded. Native arrival occurred in approximately
  0.6 seconds. The repo-built broker attached, reported the desktop profile at
  900°, and shut down through EmergencyStop without applying a force effect.
- Source state: initial uncommitted LogiControl vertical-slice worktree.
- Development registration under LogiControl CLSID
  `{32FC17A4-0050-419A-BB41-59B228B5CFF4}` passed. Both x86 and x64
  DirectInput enumerated the wheel as force-feedback capable and advertised
  all twelve effects without generating force. With broker force writes
  suppressed, both provider architectures validated all twelve effect shapes,
  rejected malformed periodic/custom definitions, closed IPC, and unloaded.
- DirectInput required explicit `Axes\0` object and force attributes to mark
  Generic Desktop/X as the actuator. The x64 runtime then created effects by
  DirectInput object ID. Physical constant, 5 Hz sine-periodic, spring, and
  damper tests all passed at a 30% ceiling for one second each.
- The 900° steering range was physically verified.
- Observation in `joy.cpl` showed that HID exposes only the final
  left-to-center portion of the firmware sweep. The agent now gates the C294
  mode switch until its 10-bit X axis shows meaningful movement and then
  remains near center (approximately 512) for 350 ms. Two consecutive physical
  runs passed: calibration completed at `last=514` and `last=520`, location-path
  correlation found C29A, the broker reopened its HID handles, and the 900°
  profile was physically correct after reconnect.
- The Bus exposed a single-digit-frame-rate regression with the initial 2 ms,
  synchronous provider render path. Suppressing broker HID force writes did not
  change it, isolating the problem above the HID transport. Both Steam and The
  Bus loaded the provider while the temporary broker exposed only one FFB pipe
  instance. The provider now coalesces callbacks into an 8 ms mixer tick,
  executes broker IPC after releasing its effect-state lock, and opens the FFB
  pipe lazily only when an effect starts. The Bus subsequently loaded normally
  and produced force feedback with hardware writes enabled.
- Still required: ETS2, physical gain checks, x86 runtime effect playback, and
  explicit crash-lease timing. Unplug/replug recovery and range restoration
  now pass.

## Deferred Phase 2 DFGT acceptance

- Replay the corrected slot-zero lifecycle: first nonzero software force uses
  `Start`, subsequent values use `Update`, and idle/StopAll resets require a new
  `Start`. Automated fake-transport tests cover the normal sequence and a
  gated stale-force race; physical behavior remains unverified.
- Confirm DirectInput one-axis directions supplied as `1`, `-1`, and `0`
  produce full positive, full negative, and legacy-positive orientation after
  native sign normalization. Native marshalling vectors cover this without
  generating force.
- Run an infinite periodic effect beyond the former 71.6-minute fixed-point
  rollover and confirm phase continuity. Deterministic fake-clock tests cover
  the rollover boundary; long-running hardware output remains unverified.
- Mode switch, calibration, managed attach initialization, StopAll, 2 ms
  runtime, range, reconnect, removal, and HID-fault recovery.
- Low-force constant, ramp, square, sine, triangle, sawtooth-up/down, custom,
  spring, damper, native friction, and inertia-to-damper mapping.
- Three simultaneous condition reservations and refusal of a fourth.
- Gain order, direction, envelope, delay, iterations, finite completion,
  pause/continue, actuator mute, idle autocenter, and dynamic range boundary.
- x86/x64 playback plus provider crash, pipe loss, broker termination, 350 ms
  lease timing, and unplug during output.
- Ten-minute profiling capture, The Bus, and ETS2 on the new path.
