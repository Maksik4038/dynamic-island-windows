# ============================================================================
#  Dynamic Island for Windows 11 - installer
#
#  Copies the widget to %LOCALAPPDATA%\DynamicIsland, creates Start Menu and
#  Desktop shortcuts, and registers it to start with Windows (HKCU Run key -
#  no administrator rights needed).
#
#  Usage:
#     powershell -ExecutionPolicy Bypass -File Install.ps1
#     powershell -ExecutionPolicy Bypass -File Install.ps1 -NoAutoStart
#     powershell -ExecutionPolicy Bypass -File Install.ps1 -Uninstall
# ============================================================================
[CmdletBinding()]
param(
    [switch]$NoAutoStart,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$AppName     = 'DynamicIsland'
$DisplayName = 'Dynamic Island'
$TargetDir   = Join-Path $env:LOCALAPPDATA $AppName
$ExeName     = 'DynamicIsland.exe'
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$SourceDir   = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step($text) { Write-Host "  $text" }
function Write-Head($text) { Write-Host ""; Write-Host $text -ForegroundColor Cyan }

function Get-InstalledPath { Join-Path $TargetDir $ExeName }

function Stop-Running {
    $procs = Get-Process -Name 'DynamicIsland' -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Step "stopping $($procs.Count) running instance(s)"
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 350
    }
}

function New-Shortcut([string]$LinkPath, [string]$TargetPath, [string]$WorkingDir) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($LinkPath)
    $sc.TargetPath = $TargetPath
    $sc.WorkingDirectory = $WorkingDir
    $sc.Description = 'Dynamic Island - iPhone-style pill for Windows 11'
    $sc.IconLocation = "$TargetPath,0"
    $sc.Save()
}

# --------------------------------------------------------------------- uninstall
if ($Uninstall) {
    Write-Head "Uninstalling $DisplayName"
    Stop-Running

    try {
        Remove-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue
        Write-Step 'removed autostart entry'
    } catch { Write-Step "autostart entry: $($_.Exception.Message)" }

    $links = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) "$DisplayName.lnk"),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "$DisplayName.lnk")
    )
    foreach ($l in $links) {
        if (Test-Path -LiteralPath $l) { Remove-Item -LiteralPath $l -Force; Write-Step "removed $l" }
    }
    if (Test-Path -LiteralPath $TargetDir) {
        Remove-Item -LiteralPath $TargetDir -Recurse -Force
        Write-Step "removed $TargetDir"
    }
    Write-Host ""
    Write-Host "Done. $DisplayName has been removed." -ForegroundColor Green
    exit 0
}

# ----------------------------------------------------------------------- install
Write-Head "Installing $DisplayName"

$sourceExe = Join-Path $SourceDir $ExeName
if (-not (Test-Path -LiteralPath $sourceExe)) {
    Write-Host "Cannot find $ExeName next to this script ($SourceDir)." -ForegroundColor Red
    exit 1
}

Stop-Running

if (-not (Test-Path -LiteralPath $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Write-Step "created $TargetDir"
}

$targetExe = Get-InstalledPath
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
Write-Step "copied $ExeName ($([math]::Round((Get-Item $targetExe).Length / 1KB, 1)) KB)"

# ship the design snapshots alongside, if present (handy reference)
$shotSrc = Join-Path $SourceDir 'shots'
if (Test-Path -LiteralPath $shotSrc) {
    $shotDst = Join-Path $TargetDir 'shots'
    if (-not (Test-Path -LiteralPath $shotDst)) { New-Item -ItemType Directory -Path $shotDst -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $shotSrc '*.png') -Destination $shotDst -Force -ErrorAction SilentlyContinue
}

# shortcuts: Start Menu always, Desktop too
try {
    $programs = [Environment]::GetFolderPath('Programs')
    New-Shortcut (Join-Path $programs "$DisplayName.lnk") $targetExe $TargetDir
    Write-Step 'created Start Menu shortcut'
} catch { Write-Step "Start Menu shortcut failed: $($_.Exception.Message)" }

try {
    $desktop = [Environment]::GetFolderPath('Desktop')
    New-Shortcut (Join-Path $desktop "$DisplayName.lnk") $targetExe $TargetDir
    Write-Step 'created Desktop shortcut'
} catch { Write-Step "Desktop shortcut failed: $($_.Exception.Message)" }

# autostart via HKCU Run (no elevation needed)
if ($NoAutoStart) {
    Remove-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue
    Write-Step 'autostart left disabled'
} else {
    if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
    Set-ItemProperty -Path $RunKey -Name $AppName -Value ('"' + $targetExe + '"')
    Write-Step 'registered to start with Windows'
}

Write-Host ""
Write-Host "Installed to $TargetDir" -ForegroundColor Green
Write-Host ""
Write-Host "Launching now..." -ForegroundColor Cyan
Start-Process -FilePath $targetExe
Start-Sleep -Milliseconds 600

Write-Host ""
Write-Host "Look at the top centre of your screen: a black pill with the time." -ForegroundColor White
Write-Host ""
Write-Host "  hover it      expand / collapse"       -ForegroundColor Gray
Write-Host "  drag a bar    set volume or screen brightness" -ForegroundColor Gray
Write-Host "  click speaker mute / unmute"        -ForegroundColor Gray
Write-Host "  tray icon     right-click for Center, autostart and Quit" -ForegroundColor Gray
Write-Host ""
Write-Host "To remove it later:  powershell -ExecutionPolicy Bypass -File `"$SourceDir\Install.ps1`" -Uninstall" -ForegroundColor Gray
