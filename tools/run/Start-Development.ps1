# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [switch]$FakeHid,
    [switch]$Profile
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$brokerPath = Join-Path $repoRoot 'src\LogiControl.Broker\bin\Release\net10.0-windows\LogiControl.Broker.dll'
if (-not (Test-Path -LiteralPath $brokerPath -PathType Leaf)) {
    throw "Build artifact is missing: $brokerPath"
}

$brokerArguments = @($brokerPath, 'serve')
if ($FakeHid) { $brokerArguments += '--fake-hid' }
if ($Profile) { $brokerArguments += '--profile' }
& dotnet @brokerArguments
