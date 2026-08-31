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

Phase 1 retains a temporary native legacy broker and provider-side mixer to
reduce uncertainty during the first physical DFGT proof. A managed device agent
owns discovery, identification, mode switching, and legacy-broker supervision.

Phase 2 moves device lifecycle and FFB state into `LogiControl.Broker`, changes
the provider pipe to semantic effect operations, and removes the legacy broker
and device agent.

## Identity model

`WheelModel`, `UsbPresentationMode`, and a connected HID interface are distinct.
A compatibility PID can be shared by several physical models. Identification
therefore combines VID/PID, revision matcher, and connection evidence.

## Failure rules

- Provider disconnect: destroy its session and send StopAll.
- Force heartbeat expiry: send StopAll and mark the session failed.
- Device removal: cancel I/O, close handles, clear effects, and await arrival.
- Invalid IPC or effect data: reject before any HID write.
- Broker restart: start disconnected, discover afresh, and send StopAll before
  accepting force.
- Unknown model/revision: diagnostics only; never switch or generate force.
