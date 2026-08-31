# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [switch]$ObserveOnly,
    [switch]$SuppressForceWrites
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$brokerPath = Join-Path $repoRoot 'out\build\windows-x64\native\LogiControl.LegacyBroker\Release\LogiControl.LegacyBroker.exe'
$agentPath = Join-Path $repoRoot 'src\LogiControl.DeviceAgent\bin\Release\net10.0-windows\LogiControl.DeviceAgent.dll'
foreach ($artifact in @($brokerPath, $agentPath)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Build artifact is missing: $artifact" }
}

$brokerArgument = if ($SuppressForceWrites) { '--diagnostics-no-force' } else { '--diagnostics' }
$broker = Start-Process -FilePath $brokerPath -ArgumentList $brokerArgument -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 400
    $agentArguments = @($agentPath, 'run')
    if ($ObserveOnly) { $agentArguments += '--observe-only' }
    & dotnet @agentArguments
}
finally {
    try { & dotnet $agentPath emergency-stop | Out-Null } catch { Write-Warning $_ }
    if (-not $broker.HasExited) { Stop-Process -Id $broker.Id }
    $broker.WaitForExit(2000)
}
