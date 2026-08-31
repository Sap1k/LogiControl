# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifestPath = Join-Path $repoRoot 'artifacts\development-registration.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Development registration manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$clsid = '{32FC17A4-0050-419A-BB41-59B228B5CFF4}'
if ([string]$manifest.clsid -ne $clsid) { throw 'Registration manifest CLSID is not owned by LogiControl.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $WhatIfPreference -and
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run unregistration from an elevated PowerShell process.'
}

$roots = @(
    'HKLM:\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C29A\OEMForceFeedback',
    'HKCU:\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C29A\OEMForceFeedback'
)
$classes = @(
    "HKLM:\SOFTWARE\Classes\CLSID\$clsid",
    "HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid"
)
$axisAttributes = [byte[]](1,1,0,0,1,0,48,0)
$axisFfAttributes = [byte[]](10,0,0,0,0,1,0,0)

function Test-RegistryKeyEmpty {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $key = Get-Item -LiteralPath $Path
    return $key.GetValueNames().Count -eq 0 -and $key.GetSubKeyNames().Count -eq 0
}

if ($PSCmdlet.ShouldProcess('VID_046D&PID_C29A', 'Remove LogiControl development registration and restore backups')) {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            $owner = (Get-ItemProperty -LiteralPath $root -Name CLSID -ErrorAction SilentlyContinue).CLSID
            if ($owner -eq $clsid) { Remove-Item -LiteralPath $root -Recurse -Force }
            else { Write-Warning "Skipping registration owned by $owner at $root" }
        }
    }
    foreach ($class in $classes) {
        if (Test-Path -LiteralPath $class) { Remove-Item -LiteralPath $class -Recurse -Force }
    }
    if ($manifest.PSObject.Properties.Name -contains 'axisChanges') {
        foreach ($change in @($manifest.axisChanges)) {
            $path = [string]$change.path
            if (Test-Path -LiteralPath $path) {
                $attributes = (Get-ItemProperty -LiteralPath $path -Name Attributes -ErrorAction SilentlyContinue).Attributes
                if ($null -ne $attributes -and
                    [BitConverter]::ToString([byte[]]$attributes) -eq [BitConverter]::ToString($axisAttributes)) {
                    Remove-ItemProperty -LiteralPath $path -Name Attributes
                } elseif ($null -ne $attributes) {
                    Write-Warning "Skipping Attributes no longer owned by LogiControl at $path"
                }
                $current = (Get-ItemProperty -LiteralPath $path -Name FFAttributes -ErrorAction SilentlyContinue).FFAttributes
                if ($null -ne $current -and
                    [BitConverter]::ToString([byte[]]$current) -eq [BitConverter]::ToString($axisFfAttributes)) {
                    Remove-ItemProperty -LiteralPath $path -Name FFAttributes
                } elseif ($null -ne $current) {
                    Write-Warning "Skipping FFAttributes no longer owned by LogiControl at $path"
                }
                if (-not [bool]$change.pathExisted -and (Test-RegistryKeyEmpty -Path $path)) {
                    Remove-Item -LiteralPath $path
                }
            }
            $parentPath = [string]$change.parentPath
            if (-not [bool]$change.parentExisted -and (Test-RegistryKeyEmpty -Path $parentPath)) {
                Remove-Item -LiteralPath $parentPath
            }
        }
    }
    foreach ($backup in @($manifest.backupFiles)) {
        $resolved = [IO.Path]::GetFullPath([string]$backup)
        $expectedRoot = [IO.Path]::GetFullPath([string]$manifest.backupRoot)
        if (-not $resolved.StartsWith($expectedRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $resolved -PathType Leaf) -or
            [IO.Path]::GetExtension($resolved) -ne '.reg') {
            throw "Refusing unexpected registry backup: $resolved"
        }
        & reg.exe import $resolved | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Registry restore failed: $resolved" }
    }
    Remove-Item -LiteralPath $manifestPath -Force
}

[pscustomobject]@{ changed = -not [bool]$WhatIfPreference; restored = @($manifest.backupFiles).Count } |
    ConvertTo-Json
