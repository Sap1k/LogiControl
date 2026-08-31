# LogiControl

LogiControl is an experimental open-source, user-mode force-feedback and
control stack for classic Logitech racing wheels on modern Windows.

The project is designed to work with Microsoft's standard HID stack while
Memory Integrity/HVCI remains enabled. It will not ship or require a custom
kernel driver.

## Status

The first DFGT vertical slice works on physical hardware. A Driving Force GT
observed as `VID_046D&PID_C294`, revision `1326`, waits for its firmware
calibration, switches to native `PID_C29A`, and attaches to the repo-built
legacy broker using only the Microsoft HID stack. x64 DirectInput constant,
periodic, spring, and damper effects, The Bus, repeated unplug/replug recovery,
and 900-degree range restoration have passed. ETS2, x86 physical playback, and
explicit crash-lease timing remain to be recorded.

This is development evidence, not a general hardware-support claim.
See [the implementation plan](docs/implementation-plan.md) and
[hardware matrix](docs/hardware-matrix.md).

Current development is hardware-free until the maintainer announces that a
physical wheel is available again. New behavior is validated with deterministic
tests and recorded traces, then queued for a later hardware replay.

## Architecture

```text
Game -> DirectInput -> x86/x64 provider -> local IPC -> .NET broker
     -> Logitech protocol -> Windows HID APIs -> wheel
```

Input continues to flow through the standard Windows HID and DirectInput stack.
LogiControl supplies only missing Logitech-specific mode, control, and
force-feedback behavior.

## Build

Managed prerequisites:

- .NET 10 SDK

```powershell
dotnet build .\LogiControl.slnx -c Release
dotnet run --project .\tests\LogiControl.Protocol.Tests -c Release
```

Native prerequisites:

- Visual Studio 2026 with Desktop development with C++
- Windows SDK
- CMake 3.24 or newer

```powershell
.\tools\build\Build.ps1 -Configuration Release
```

The helper discovers the CMake bundled with Visual Studio through `vswhere`.

Read-only hardware discovery:

```powershell
dotnet run --project .\src\LogiControl.DeviceAgent -c Release -- list --json
dotnet run --project .\src\LogiControl.DeviceAgent -c Release -- run --observe-only
```

Development registration requires an elevated PowerShell and supports
`-WhatIf`. Physical-force testing is a separate explicit harness action.

## Safety

Physical-force tests are never part of normal CI. Unknown compatibility-mode
devices are never written to. Read [the hardware safety rules](docs/safety-testing.md)
before adding or running device-output code.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and
[third-party notices](THIRD_PARTY_NOTICES.md).
