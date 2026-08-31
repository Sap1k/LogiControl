# LogiControl

LogiControl is an experimental open-source, user-mode force-feedback and
control stack for classic Logitech racing wheels on modern Windows.

The project is designed to work with Microsoft's standard HID stack while
Memory Integrity/HVCI remains enabled. It will not ship or require a custom
kernel driver.

## Status

The first DFGT vertical slice worked on physical hardware. Phase 2 now replaces
that legacy path with the authoritative .NET broker, a 500 Hz effect engine,
semantic IPC, and asynchronous HID output. The new path is automated-test-only
until the next physical-wheel replay; the earlier x64 DirectInput and real-game
results remain historical baseline evidence.

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
dotnet test .\LogiControl.slnx -c Release
```

Native prerequisites:

- Visual Studio 2026 with Desktop development with C++
- Windows SDK
- CMake 3.24 or newer

```powershell
.\tools\build\Build.ps1 -Configuration Release
```

The helper discovers the CMake bundled with Visual Studio through `vswhere`.

Broker diagnostics (do not run hardware-serving mode without the wheel):

```powershell
dotnet run --project .\src\LogiControl.Broker -c Release -- list --json
dotnet run --project .\src\LogiControl.Broker -c Release -- status
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
