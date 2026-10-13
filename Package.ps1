<#
  Packs a ready-to-run DynamicIsland.zip for people who just want to use the app.

  The point is that nobody should need a compiler or the Windows SDK: the archive
  contains the built executable plus the scripts that matter at runtime.

      powershell -ExecutionPolicy Bypass -File Package.ps1
      powershell -ExecutionPolicy Bypass -File Package.ps1 -Version 1.0.0

  Produces release/DynamicIsland.zip
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------------------------------------------------------------- build first
$exe = Join-Path $root 'DynamicIsland.exe'
Write-Host "building a fresh executable..." -ForegroundColor Cyan
& (Join-Path $root 'Build.ps1') | Out-Null
if (-not (Test-Path -LiteralPath $exe)) { throw "build did not produce DynamicIsland.exe" }

# ---------------------------------------------------------------- stage
if (-not $OutDir) { $OutDir = Join-Path $root 'release' }
$stageRoot = Join-Path $OutDir 'DynamicIsland'
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

# everything a user needs to run, install and troubleshoot the app
$files = @(
    'DynamicIsland.exe',
    'README.md',
    'COMMANDS.md',
    'LICENSE',
    'Install.ps1',
    'Diagnose.ps1',
    'CheckBlocked.ps1',
    'Run.cmd',
    'Build.ps1'
)
foreach ($f in $files) {
    $src = Join-Path $root $f
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $stageRoot }
    else { Write-Warning "missing: $f" }
}

$shots = Join-Path $root 'shots'
if (Test-Path -LiteralPath $shots) {
    $shotDest = Join-Path $stageRoot 'shots'
    New-Item -ItemType Directory -Force -Path $shotDest | Out-Null
    # note: -Path, not -LiteralPath - a wildcard has to be expanded by the provider
    $pngs = @(Get-ChildItem -Path (Join-Path $shots '*.png') -File -ErrorAction SilentlyContinue)
    foreach ($png in $pngs) { Copy-Item -LiteralPath $png.FullName -Destination $shotDest }
    Write-Host "  screenshots: $($pngs.Count)"
}

# a short starter note on top of the full README
$versionLine = if ($Version) { "Version $Version`r`n" } else { "" }
@"
Dynamic Island for Windows 11
$versionLine
QUICK START
-----------
1. Unpack this folder anywhere.
2. Run DynamicIsland.exe

   The island appears at the very top centre of the screen. Right-click the
   tray icon to change the resting width, enable autostart or quit.

3. Optional - install it properly (Start Menu shortcut + autostart):

       powershell -ExecutionPolicy Bypass -File Install.ps1

   To remove it again:

       powershell -ExecutionPolicy Bypass -File Install.ps1 -Uninstall

IF IT DOES NOT START
--------------------
Run the diagnostic; it checks the usual causes and prints a self test:

       powershell -ExecutionPolicy Bypass -File Diagnose.ps1

A startup crash is also written to island-error.log next to the exe.

REQUIREMENTS
------------
Windows 11 and the .NET Framework 4.x, which ships with Windows.
The executable is already built - no compiler and no Windows SDK needed.

Full documentation: README.md
All command-line activities: COMMANDS.md
"@ | Set-Content -LiteralPath (Join-Path $stageRoot 'START-HERE.txt') -Encoding UTF8

# ---------------------------------------------------------------- zip
$zip = Join-Path $OutDir 'DynamicIsland.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $stageRoot -DestinationPath $zip -Force

$zipItem = Get-Item -LiteralPath $zip
$size = [math]::Round($zipItem.Length / 1KB, 1)
$exeSize = [math]::Round((Get-Item -LiteralPath $exe).Length / 1KB, 1)

Write-Host ""
Write-Host "OK - packaged" -ForegroundColor Green
Write-Host "  archive : $zip  ($size KB)"
Write-Host "  exe     : $exeSize KB"
Write-Host "  contents: $((Get-ChildItem -LiteralPath $stageRoot -Recurse -File).Count) files"
Write-Host ""
Write-Host "Upload this zip to a GitHub Release so users can download it directly:" -ForegroundColor Cyan
Write-Host "  https://github.com/Maksik4038/dynamic-island-windows/releases/new"
