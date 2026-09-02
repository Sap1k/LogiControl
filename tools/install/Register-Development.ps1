# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$ReplaceExistingProvider,
    [switch]$PlanOnly,
    [string]$Provider64,
    [string]$Provider32
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Provider64) {
    $Provider64 = Join-Path $repoRoot 'out\build\windows-x64\native\LogiControl.Ffb\Release\LogiControl.Ffb.dll'
}
if (-not $Provider32) {
    $Provider32 = Join-Path $repoRoot 'out\build\windows-x86\native\LogiControl.Ffb\Release\LogiControl.Ffb.dll'
}
$productIds = @('C29A', 'C29B', 'C299', 'C298')
$clsid = '{32FC17A4-0050-419A-BB41-59B228B5CFF4}'
$oemBase = 'SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM'
$oemRoots = foreach ($hive in @('HKLM:', 'HKCU:')) {
    foreach ($productId in $productIds) {
        "$hive\$oemBase\VID_046D&PID_$productId"
    }
}
$forceFeedbackRoots = @($oemRoots | ForEach-Object { Join-Path $_ 'OEMForceFeedback' })
$axisRoots = @($oemRoots | ForEach-Object { Join-Path $_ 'Axes' })
$class64 = "HKLM:\SOFTWARE\Classes\CLSID\$clsid"
$class32 = "HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid"

if ($PlanOnly) {
    [ordered]@{
        changed = $false
        clsid = $clsid
        productIds = $productIds
        oemRoots = $oemRoots
        forceFeedbackRoots = $forceFeedbackRoots
        axisRoots = $axisRoots
        classRoots = @($class64, $class32)
        operationOrder = @('conflict-check', 'backup', 'com', 'oem-force-feedback', 'axes', 'manifest')
        conflictPolicy = 'refuse-unless-explicit-replace'
        uninstallPolicy = 'manifest-owned-only'
    } | ConvertTo-Json -Depth 4
    return
}

foreach ($provider in @($Provider64, $Provider32)) {
    if (-not (Test-Path -LiteralPath $provider -PathType Leaf)) {
        throw "Provider artifact is missing: $provider"
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $WhatIfPreference -and
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run registration from an elevated PowerShell process.'
}

$manifestPath = Join-Path $repoRoot 'artifacts\development-registration.json'
$existingManifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $existingManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$existingManifest.clsid -ne $clsid) {
        throw 'Existing development registration manifest is not owned by LogiControl.'
    }
    if (-not ($existingManifest.PSObject.Properties.Name -contains 'productIds') -or
        @($existingManifest.productIds).Count -ne $productIds.Count) {
        throw 'The existing manifest predates Phase 3. Unregister it before installing the four-wheel plan.'
    }
}
$backupRoot = if ($null -ne $existingManifest) {
    [string]$existingManifest.backupRoot
} else {
    Join-Path $repoRoot ('artifacts\registration-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$axisAttributes = [byte[]](1,1,0,0,1,0,48,0) # FFACTUATOR | ASPECTPOSITION, Generic Desktop/X
$axisFfAttributes = [byte[]](10,0,0,0,0,1,0,0) # 10 N maximum, 256 force gradations

foreach ($root in $forceFeedbackRoots) {
    if (Test-Path -LiteralPath $root) {
        $owner = (Get-ItemProperty -LiteralPath $root -Name CLSID -ErrorAction SilentlyContinue).CLSID
        if ($owner -and $owner -ne $clsid -and -not $ReplaceExistingProvider) {
            throw "The registration at $root is owned by $owner. Re-run with -ReplaceExistingProvider only after reviewing the backup path."
        }
    }
}

function New-EffectAttributes {
    param([uint32]$Id, [uint32]$Type, [uint32]$Parameters)
    $result = [byte[]]::new(20)
    $values = @($Id, $Type, $Parameters, $Parameters, [uint32]0x10)
    for ($index = 0; $index -lt $values.Count; $index++) {
        [BitConverter]::GetBytes([uint32]$values[$index]).CopyTo($result, $index * 4)
    }
    return ,$result
}

$software = [uint32]0x000003E5
$condition = [uint32]0x00000365
$effects = @(
    @{ Guid='{13541C20-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Constant';      Id=0;     Type=0x00008601; Parameters=$software },
    @{ Guid='{13541C21-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Ramp Force';    Id=1;     Type=0x00008602; Parameters=$software },
    @{ Guid='{13541C22-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Square Wave';   Id=2;     Type=0x00008603; Parameters=$software },
    @{ Guid='{13541C23-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Sine Wave';     Id=3;     Type=0x00008603; Parameters=$software },
    @{ Guid='{13541C24-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Triangle Wave'; Id=4;     Type=0x00008603; Parameters=$software },
    @{ Guid='{13541C25-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Sawtooth Up';   Id=5;     Type=0x00008603; Parameters=$software },
    @{ Guid='{13541C26-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Sawtooth Down'; Id=6;     Type=0x00008603; Parameters=$software },
    @{ Guid='{13541C27-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Spring';        Id=7;     Type=0x0000D804; Parameters=$condition },
    @{ Guid='{13541C28-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Damper';        Id=8;     Type=0x0000D804; Parameters=$condition },
    @{ Guid='{13541C29-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Inertia';       Id=9;     Type=0x0000D804; Parameters=$condition },
    @{ Guid='{13541C2A-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Friction';      Id=10;    Type=0x0000D804; Parameters=$condition },
    @{ Guid='{13541C2B-8E33-11D0-9AD0-00A0C9A06E35}'; Name='Custom Force';  Id=0x100; Type=0x00008605; Parameters=([uint32]($software -bor 2)) }
)

if ($PSCmdlet.ShouldProcess(($productIds -join ', '), 'Register LogiControl development DirectInput provider')) {
    if ($null -eq $existingManifest) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $exports = @()
        foreach ($hive in @('HKLM', 'HKCU')) {
            foreach ($productId in $productIds) {
                $exports += @{
                    Native="$hive\$oemBase\VID_046D&PID_$productId"
                    File="oem-$($hive.ToLowerInvariant())-$($productId.ToLowerInvariant()).reg"
                }
            }
        }
        $exports += @(
            @{ Native="HKLM\SOFTWARE\Classes\CLSID\$clsid"; File='class64.reg' },
            @{ Native="HKLM\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid"; File='class32.reg' }
        )
        $backupFiles = @()
        foreach ($export in $exports) {
            $registryPath = 'Registry::HKEY_' + ($export.Native -replace '^HKLM', 'LOCAL_MACHINE' -replace '^HKCU', 'CURRENT_USER')
            if (Test-Path -LiteralPath $registryPath) {
                $file = Join-Path $backupRoot $export.File
                & reg.exe export $export.Native $file /y | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Registry backup failed: $($export.Native)" }
                $backupFiles += $file
            }
        }
    } else {
        $backupFiles = @($existingManifest.backupFiles)
    }

    foreach ($class in @(
        @{ Root=$class64; Dll=(Resolve-Path $Provider64).Path },
        @{ Root=$class32; Dll=(Resolve-Path $Provider32).Path }
    )) {
        $inProc = Join-Path $class.Root 'InProcServer32'
        New-Item -Path $inProc -Force | Out-Null
        Set-Item -LiteralPath $class.Root -Value 'LogiControl DirectInput FFB Provider'
        Set-Item -LiteralPath $inProc -Value $class.Dll
        New-ItemProperty -LiteralPath $inProc -Name ThreadingModel -Value Both -PropertyType String -Force | Out-Null
    }

    foreach ($root in $forceFeedbackRoots) {
        $effectsRoot = Join-Path $root 'Effects'
        New-Item -Path $root -Force | Out-Null
        New-ItemProperty -LiteralPath $root -Name Attributes -Value ([byte[]](0,0,0,0,64,31,0,0,64,31,0,0)) -PropertyType Binary -Force | Out-Null
        New-ItemProperty -LiteralPath $root -Name CLSID -Value $clsid -PropertyType String -Force | Out-Null
        if (Test-Path -LiteralPath $effectsRoot) { Remove-Item -LiteralPath $effectsRoot -Recurse -Force }
        New-Item -Path $effectsRoot -Force | Out-Null
        foreach ($effect in $effects) {
            $effectPath = Join-Path $effectsRoot $effect.Guid
            New-Item -Path $effectPath -Force | Out-Null
            Set-Item -LiteralPath $effectPath -Value $effect.Name
            New-ItemProperty -LiteralPath $effectPath -Name Attributes `
                -Value (New-EffectAttributes $effect.Id $effect.Type $effect.Parameters) `
                -PropertyType Binary -Force | Out-Null
        }
    }

    $axisChanges = @()
    if ($null -ne $existingManifest -and
        $existingManifest.PSObject.Properties.Name -contains 'axisChanges') {
        $axisChanges = @($existingManifest.axisChanges)
    }
    if ($axisChanges.Count -eq 0) {
        foreach ($axisRoot in $axisRoots) {
            $axisPath = Join-Path $axisRoot '0'
            $axisChanges += [ordered]@{
                path = $axisPath
                pathExisted = Test-Path -LiteralPath $axisPath
                parentPath = $axisRoot
                parentExisted = Test-Path -LiteralPath $axisRoot
            }
        }
    }
    foreach ($axisRoot in $axisRoots) {
        $axisPath = Join-Path $axisRoot '0'
        New-Item -Path $axisPath -Force | Out-Null
        New-ItemProperty -LiteralPath $axisPath -Name Attributes `
            -Value $axisAttributes -PropertyType Binary -Force | Out-Null
        New-ItemProperty -LiteralPath $axisPath -Name FFAttributes `
            -Value $axisFfAttributes -PropertyType Binary -Force | Out-Null
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $manifestPath) -Force | Out-Null
    [ordered]@{
        registeredAt = (Get-Date).ToString('o')
        clsid = $clsid
        provider64 = (Resolve-Path $Provider64).Path
        provider32 = (Resolve-Path $Provider32).Path
        productIds = $productIds
        oemRoots = $oemRoots
        forceFeedbackRoots = $forceFeedbackRoots
        classRoots = @($class64, $class32)
        backupFiles = $backupFiles
        backupRoot = $backupRoot
        axisChanges = $axisChanges
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}

[pscustomobject]@{
    changed = -not [bool]$WhatIfPreference
    clsid = $clsid
    productIds = $productIds
    backupRoot = $backupRoot
} |
    ConvertTo-Json
