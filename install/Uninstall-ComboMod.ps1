<#
.SYNOPSIS
    Removes ComboMod, and optionally BepInEx, from Combolands.

.DESCRIPTION
    Removes only what the installer put there.

    Balance packs live inside BepInEx\config, so removing BepInEx necessarily removes
    them too. Rather than pretend otherwise, this copies any packs you have written to a
    dated folder in your Documents first, and tells you where. Losing hand-authored
    tuning to an uninstall would be a bad way to find out how it works.

    The game itself is never modified by any of this -- no game file is patched on disk,
    so Steam's integrity check stays green either way.

.PARAMETER GamePath
    Override auto-detection. Point at the folder containing Combolands.exe.

.PARAMETER KeepBepInEx
    Remove ComboMod's plugins but leave BepInEx in place, for when other mods use it.

.PARAMETER Purge
    Skip the backup and delete your balance packs outright. Irreversible.

.EXAMPLE
    .\Uninstall-ComboMod.ps1
    .\Uninstall-ComboMod.ps1 -KeepBepInEx
    .\Uninstall-ComboMod.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$KeepBepInEx,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'

$SteamAppId = '4075620'

function Write-Step { param($m) Write-Host "  $m" }
function Write-Good { param($m) Write-Host "  $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Write-Bad  { param($m) Write-Host "  $m" -ForegroundColor Red }

function Find-GameFolder {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }

    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path $vdf)) { return $null }

    $libraries = Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' |
                 ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }

    foreach ($library in $libraries) {
        $manifest = Join-Path $library "steamapps\appmanifest_$SteamAppId.acf"
        if (-not (Test-Path $manifest)) { continue }

        $match = Select-String -Path $manifest -Pattern '"installdir"\s+"(.+?)"'
        if (-not $match) { continue }

        $candidate = Join-Path $library ("steamapps\common\" + $match.Matches[0].Groups[1].Value)
        if (Test-Path (Join-Path $candidate 'Combolands.exe')) { return $candidate }
    }

    return $null
}

function Remove-IfPresent {
    param($Path, $Label)
    if (-not (Test-Path $Path)) { return $false }

    Remove-Item -Path $Path -Recurse -Force
    Write-Good "removed $Label"
    return $true
}

# ---------------------------------------------------------------- main

Write-Host ""
Write-Host "ComboMod uninstaller" -ForegroundColor Cyan
Write-Host ""

if (-not $GamePath) {
    Write-Step "Looking for Combolands..."
    $GamePath = Find-GameFolder
}

if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Combolands.exe'))) {
    Write-Bad "Could not find Combolands."
    Write-Host '    .\Uninstall-ComboMod.ps1 -GamePath "D:\Games\Combolands"' -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Good "Found: $GamePath"

if (Get-Process -Name 'Combolands' -ErrorAction SilentlyContinue) {
    Write-Bad "Combolands is running. Close it and run this again."
    Write-Host ""
    exit 1
}

$config = Join-Path $GamePath 'BepInEx\config\ComboMod'
$packs  = Join-Path $config 'packs'

# Rescue user-authored packs before anything is deleted.
#
# This matters more than it first appears: packs live under BepInEx\config, so removing
# BepInEx removes them as a side effect even when the user never asked for that. The
# backup goes to Documents rather than TEMP, because TEMP is exactly where a file goes
# to be deleted by the next disk cleanup.
$rescued = $null
if (-not $Purge -and (Test-Path $packs)) {
    $packFiles = Get-ChildItem -Path $packs -Filter '*.pack' -ErrorAction SilentlyContinue
    if ($packFiles) {
        $documents = [System.Environment]::GetFolderPath('MyDocuments')
        $rescued = Join-Path $documents "ComboMod-packs-$(Get-Date -Format 'yyyy-MM-dd-HHmm')"
        New-Item -ItemType Directory -Path $rescued -Force | Out-Null
        $packFiles | Copy-Item -Destination $rescued -Force
        Write-Good "saved $($packFiles.Count) pack(s) to $rescued"
    }
}

try {
    $removed = $false
    $removed = (Remove-IfPresent (Join-Path $GamePath 'BepInEx\plugins\ComboMod') 'ComboMod plugins') -or $removed

    if ($Purge) {
        $removed = (Remove-IfPresent $config 'ComboMod packs and settings') -or $removed
        $removed = (Remove-IfPresent (Join-Path $GamePath 'BepInEx\config\dev.combolands.combomod.core.cfg')   'Core settings')   -or $removed
        $removed = (Remove-IfPresent (Join-Path $GamePath 'BepInEx\config\dev.combolands.combomod.editor.cfg') 'Editor settings') -or $removed
        $removed = (Remove-IfPresent (Join-Path $GamePath 'BepInEx\config\dev.combolands.combomod.cheats.cfg') 'Cheats settings') -or $removed
    }

    if (-not $KeepBepInEx) {
        # Only remove BepInEx if nothing else is relying on it. Deleting another mod's
        # loader out from under it would be a rude way to uninstall.
        $otherPlugins = Get-ChildItem -Path (Join-Path $GamePath 'BepInEx\plugins') -Directory -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -ne 'ComboMod' }
        $looseDlls = Get-ChildItem -Path (Join-Path $GamePath 'BepInEx\plugins') -Filter '*.dll' -ErrorAction SilentlyContinue

        if ($otherPlugins -or $looseDlls) {
            Write-Warn "Other mods are installed - leaving BepInEx in place."
        }
        else {
            $removed = (Remove-IfPresent (Join-Path $GamePath 'BepInEx')             'BepInEx')             -or $removed
            $removed = (Remove-IfPresent (Join-Path $GamePath 'winhttp.dll')         'winhttp.dll')         -or $removed
            $removed = (Remove-IfPresent (Join-Path $GamePath 'doorstop_config.ini') 'doorstop_config.ini') -or $removed
            $removed = (Remove-IfPresent (Join-Path $GamePath '.doorstop_version')   '.doorstop_version')   -or $removed
        }
    }
    else {
        Write-Step "left BepInEx in place (-KeepBepInEx)"
    }

    if (-not $removed) { Write-Warn "Nothing to remove - ComboMod was not installed here." }
}
catch {
    Write-Host ""
    Write-Bad $_.Exception.Message
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Good "Done. The game is back to vanilla."
Write-Host ""

if ($rescued) {
    Write-Warn "Your packs were moved out of the game folder to:"
    Write-Warn "  $rescued"
    Write-Step "Copy them back into BepInEx\config\ComboMod\packs if you reinstall."
    Write-Host ""
}

Write-Step "No game file was ever modified, so Steam's integrity check will pass."
Write-Step "Saves made while modded still load - base stats are never written to them."
Write-Host ""
