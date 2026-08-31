# DFGT vertical-slice development runbook

This runbook is for the single-wheel DFGT development path. It does not claim
support for another physical model or an unknown C294 revision.

## Build

Visual Studio 2026's bundled CMake is discovered through `vswhere`.

```powershell
.\tools\build\Build.ps1 -Configuration Release
```

## Read-only checks

These commands never open HID for output:

```powershell
dotnet run --project .\src\LogiControl.DeviceAgent -c Release -- list --json
dotnet run --project .\src\LogiControl.DeviceAgent -c Release -- run --observe-only
```

Stop the observer with Ctrl+C. An eligible DFGT must be reported as Logitech
C294 with a `13xx` revision, joystick usage `0001:0004`, and an output report
length of at least eight bytes.

## Development registration

Preview in a normal shell:

```powershell
.\tools\install\Register-Development.ps1 -WhatIf
```

Then run the same script from an elevated PowerShell without `-WhatIf`.
Registration applies only to C29A, backs up existing registry branches, and
records `artifacts/development-registration.json`.

To remove it from an elevated shell and restore captured registry branches:

```powershell
.\tools\install\Unregister-Development.ps1 -Confirm:$false
```

## Run the broker and agent

```powershell
.\tools\run\Start-Development.ps1
```

For C294, the agent automatically sends the two native-mode reports after
revision validation. For C29A, it attaches directly. Ctrl+C triggers
EmergencyStop and terminates the development broker.

For provider parsing/IPC diagnostics with every force write suppressed in the
broker, use `Start-Development.ps1 -SuppressForceWrites`.

## DirectInput checks

With the broker and agent running, enumeration is force-free:

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

Launch a game only after the agent reports `legacy-broker-ready`. For the
initial acceptance run, use Euro Truck Simulator 2 and preserve provider and
broker diagnostics before beginning the Phase 2 rewrite.
