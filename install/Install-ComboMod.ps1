<#
.SYNOPSIS
    Installs ComboMod and BepInEx into Combolands.

.DESCRIPTION
    Finds the game through Steam, installs BepInEx if it is not already there, and copies
    the ComboMod plugins in. Safe to run repeatedly: it only replaces what it owns and
    never touches your packs or config.

    The step people get wrong when installing a Unity mod by hand is extracting BepInEx
    one folder too deep or too shallow, which fails silently -- the game launches and
    simply nothing happens. Doing it from a script removes that failure entirely.

.PARAMETER GamePath
    Override auto-detection. Point at the folder containing Combolands.exe.

.PARAMETER SkipCheats
    Install the framework and editor but not the cheat menu (run editing, giving items).

.PARAMETER CoreOnly
    Install only the framework: balance packs work, no in-game UI at all.

.EXAMPLE
    .\Install-ComboMod.ps1
    .\Install-ComboMod.ps1 -CoreOnly
    .\Install-ComboMod.ps1 -GamePath "D:\Games\Combolands"
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$SkipCheats,
    [switch]$CoreOnly
)

$ErrorActionPreference = 'Stop'

# Pinned rather than "latest": a BepInEx major version bump could change the loader
# contract, and an installer that silently follows it would break without warning.
# The hash is the one actually verified against this mod.
$BepInExVersion = '5.4.23.3'
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_${BepInExVersion}.zip"
$BepInExSha256  = '41A089E5B1B1F0713B331346BAF6677B1184C69EABEBF51101097954E854C749'

$SteamAppId = '4075620'

function Write-Step   { param($m) Write-Host "  $m" }
function Write-Good   { param($m) Write-Host "  $m" -ForegroundColor Green }
function Write-Warn   { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Write-Bad    { param($m) Write-Host "  $m" -ForegroundColor Red }

function Find-GameFolder {
    <#
        Walks Steam's library list rather than guessing at Program Files. People move
        games to other drives constantly, and a wrong guess here produces a confusing
        "installed successfully" against a folder the game never loads from.
    #>
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

function Assert-GameClosed {
    param($Path)
    if (Get-Process -Name 'Combolands' -ErrorAction SilentlyContinue) {
        throw "Combolands is running. Close it and run this again - its files are locked while it is open."
    }
}

function Install-BepInEx {
    param($Path)

    $marker = Join-Path $Path 'winhttp.dll'
    if (Test-Path $marker) {
        Write-Good "BepInEx already installed - leaving it alone."
        return
    }

    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "BepInEx_$BepInExVersion.zip"

    Write-Step "Downloading BepInEx $BepInExVersion..."
    try {
        # TLS 1.2 for Windows PowerShell 5, which does not negotiate it by default.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
    }
    catch {
        throw "Could not download BepInEx: $($_.Exception.Message)`n  Download it yourself from $BepInExUrl and extract it into`n  $Path"
    }

    # Verify before extracting. This writes an executable loader into a game folder, so a
    # corrupted or substituted download is worth catching rather than trusting the host.
    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
    if ($actual -ne $BepInExSha256) {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        throw "BepInEx download failed verification.`n  expected $BepInExSha256`n  got      $actual`n  Nothing was installed."
    }

    Write-Step "Verified. Extracting..."
    Expand-Archive -Path $zip -DestinationPath $Path -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Write-Good "BepInEx $BepInExVersion installed."
}

function Install-Plugins {
    param($Path)

    $source = Join-Path $PSScriptRoot 'plugins'
    if (-not (Test-Path $source)) {
        throw "No plugins folder next to this script. Expected: $source"
    }

    $wanted = @('ComboMod.Core.dll')
    if (-not $CoreOnly) {
        $wanted += 'ComboMod.Editor.dll'
        if (-not $SkipCheats) { $wanted += 'ComboMod.Cheats.dll' }
    }

    $target = Join-Path $Path 'BepInEx\plugins\ComboMod'
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # Clear only our own DLLs, so switching from a full install to -CoreOnly actually
    # removes the parts you deselected instead of leaving them loaded.
    Get-ChildItem -Path $target -Filter 'ComboMod.*.dll' -ErrorAction SilentlyContinue |
        Remove-Item -Force

    foreach ($dll in $wanted) {
        $from = Join-Path $source $dll
        if (-not (Test-Path $from)) { throw "Missing $dll in $source" }
        Copy-Item -Path $from -Destination $target -Force
        Write-Good "installed $dll"
    }

    foreach ($skipped in @('ComboMod.Editor.dll', 'ComboMod.Cheats.dll')) {
        if ($wanted -notcontains $skipped) { Write-Step "skipped  $skipped" }
    }
}

# ---------------------------------------------------------------- main

Write-Host ""
Write-Host "ComboMod installer" -ForegroundColor Cyan
Write-Host ""

if (-not $GamePath) {
    Write-Step "Looking for Combolands..."
    $GamePath = Find-GameFolder
}

if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Combolands.exe'))) {
    Write-Bad "Could not find Combolands."
    Write-Host ""
    Write-Host "  Pass the folder containing Combolands.exe:" -ForegroundColor Yellow
    Write-Host '    .\Install-ComboMod.ps1 -GamePath "D:\Games\Combolands"' -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Good "Found: $GamePath"

try {
    Assert-GameClosed -Path $GamePath
    Install-BepInEx -Path $GamePath
    Install-Plugins -Path $GamePath
}
catch {
    Write-Host ""
    Write-Bad $_.Exception.Message
    Write-Host ""
    exit 1
}

Write-Host ""
if ($CoreOnly) {
    # No panel was installed, so telling someone to press F6 would just confuse them.
    Write-Good "Done. Balance packs will load on launch; there is no in-game UI."
}
else {
    Write-Good "Done. Launch the game and press F6."
}
Write-Host ""
Write-Step "Balance packs go in:"
Write-Step "  $(Join-Path $GamePath 'BepInEx\config\ComboMod\packs')"
Write-Step "To remove everything, run Uninstall-ComboMod.ps1"
Write-Host ""
