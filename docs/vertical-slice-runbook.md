# Classic-wheel development runbook

The physical results in this runbook are historical DFGT evidence. The current
broker has automated-test-only definitions for DFGT, G27, G25, and Driving
Force Pro and manages only one selected wheel at a time. Unknown revisions are
always read-only.

## Build

Visual Studio 2026's bundled CMake is discovered through `vswhere`.

```powershell
.\tools\build\Build.ps1 -Configuration Release
```

## Read-only checks

These commands never open HID for output:

```powershell
dotnet run --project .\src\LogiControl.Broker -c Release -- list --json
```

This command performs enumeration only and opens no HID output. An eligible DFGT must be reported as Logitech
C294 with a `13xx` revision, joystick usage `0001:0004`, and an output report
length of at least eight bytes.

## Development registration

Preview in a normal shell:

```powershell
.\tools\install\Register-Development.ps1 -WhatIf
```

Then run the same script from an elevated PowerShell without `-WhatIf`.
Registration applies to C29A, C29B, C299, and C298, backs up existing registry
branches before mutation, and
records `artifacts/development-registration.json`.

To remove it from an elevated shell and restore captured registry branches:

```powershell
.\tools\install\Unregister-Development.ps1 -Confirm:$false
```

## Run the managed broker

```powershell
.\tools\run\Start-Development.ps1
```

For a single recognized lower presentation, the broker calibrates and sends the
catalogued preferred-mode sequence after revision validation. It attaches
directly to a recognized preferred presentation. With multiple candidates it
remains read-only until `select <device-id>` is used. Ctrl+C triggers StopAll,
closes output, and terminates the development broker.

For provider parsing/IPC acceptance without physical enumeration or output,
use `Start-Development.ps1 -FakeHid`. Add `-Profile` only for per-event
EventSource capture; aggregate telemetry is always enabled.

From another shell, query or control the running broker with:

```powershell
dotnet run --project .\src\LogiControl.Broker -c Release -- status
dotnet run --project .\src\LogiControl.Broker -c Release -- devices
dotnet run --project .\src\LogiControl.Broker -c Release -- select auto
dotnet run --project .\src\LogiControl.Broker -c Release -- select 1
dotnet run --project .\src\LogiControl.Broker -c Release -- telemetry
dotnet run --project .\src\LogiControl.Broker -c Release -- settings
dotnet run --project .\src\LogiControl.Broker -c Release -- emergency-stop
```

## DirectInput checks

With the broker running, enumeration is force-free:

```powershell
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe enumerate
.\out\build\windows-x86\native\tests\Release\LogiControl.DirectInputHarness.exe enumerate
```

The bounded physical test must be run by a person at the wheel. It requires
both a hardware flag and the typed confirmation `YES`:

```powershell
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe pulse constant --hardware-test
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe pulse periodic --hardware-test
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe pulse spring --hardware-test
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe pulse damper --hardware-test
.\out\build\windows-x64\native\tests\Release\LogiControl.DirectInputHarness.exe pulse all --hardware-test
```

Each test is capped at 30% for one second. Periodic uses a 5 Hz sine wave;
spring should be started off-center; and the wheel should be moved during the
damper test. `pulse all` asks for `YES` only once. Passing `--confirm` supplies
that acknowledgement non-interactively while `--hardware-test` remains
mandatory. The harness stops and
unloads the effect and sends `DISFFC_STOPALL` during cleanup. It never runs in
CI.

Launch a game only after `status` reports `deviceReady: true`. Phase 2 remains
automated-test-only until the deferred matrix in `docs/hardware-matrix.md` is
replayed. Preserve provider and broker profiling data for The Bus and Euro
Truck Simulator 2.
