# ============================================================================
#  Builds DynamicIsland.exe from source.
#
#  Requirements:
#    * .NET Framework 4.x        (ships with Windows - provides csc.exe)
#    * Windows SDK 10            (provides Windows.winmd for the media API)
#
#  The SDK is only needed to COMPILE. The finished exe runs on any Windows 11
#  machine with the .NET Framework, no SDK required.
#
#  Usage:
#     powershell -ExecutionPolicy Bypass -File Build.ps1
#     powershell -ExecutionPolicy Bypass -File Build.ps1 -Run
# ============================================================================
[CmdletBinding()]
param(
    [switch]$Run,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-Compiler {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    throw "csc.exe not found. Install the .NET Framework 4.x."
}

function Find-Winmd {
    # newest SDK wins
    $bases = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata",
        "$env:ProgramFiles\Windows Kits\10\UnionMetadata"
    )
    $found = @()
    foreach ($b in $bases) {
        if (-not (Test-Path -LiteralPath $b)) { continue }
        $found += Get-ChildItem -LiteralPath $b -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^\d+\.\d+' } |
                  ForEach-Object {
                      $w = Join-Path $_.FullName 'Windows.winmd'
                      if (Test-Path -LiteralPath $w) { [pscustomobject]@{ Path = $w; Version = [version]$_.Name } }
                  }
    }
    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path
}

function Find-Facade([string]$name) {
    $c = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\$name"
    if (Test-Path -LiteralPath $c) { return $c }
    $c2 = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\$name"
    if (Test-Path -LiteralPath $c2) { return $c2 }
    return $null
}

# ---------------------------------------------------------------- sources
$sources = @(
    'DynamicIsland.cs', 'Activities.cs', 'ActivityPipe.cs', 'Privacy.cs',
    'Media.cs', 'Devices.cs', 'Sensors.cs', 'Settings.cs'
) | ForEach-Object { Join-Path $root $_ }

foreach ($s in $sources) {
    if (-not (Test-Path -LiteralPath $s)) { throw "missing source file: $s" }
}

$csc = Find-Compiler
Write-Host "compiler : $csc"

$refs = @(
    'System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Core.dll', 'System.Management.dll'
) | ForEach-Object { "/reference:$_" }

$winmd = Find-Winmd
if (-not $winmd) {
    throw "Windows.winmd not found. Install the Windows 10/11 SDK (the 'Windows SDK for Desktop C++' / .NET component is enough)."
}
Write-Host "winmd    : $winmd"
$refs += "/reference:$winmd"

$sr = Find-Facade 'System.Runtime.dll'
$wrt = Find-Facade 'System.Runtime.WindowsRuntime.dll'
if (-not $sr -or -not $wrt) {
    throw "System.Runtime.dll / System.Runtime.WindowsRuntime.dll not found in the .NET Framework folder."
}
$refs += "/reference:$sr"
$refs += "/reference:$wrt"

if (-not $OutputPath) { $OutputPath = Join-Path $root 'DynamicIsland.exe' }

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4',
    "/out:$OutputPath"
) + $refs + $sources

Write-Host "building : $OutputPath"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compilation failed with exit code $LASTEXITCODE" }

$size = [math]::Round((Get-Item -LiteralPath $OutputPath).Length / 1KB, 1)
Write-Host ""
Write-Host "OK - built $OutputPath ($size KB)" -ForegroundColor Green

if ($Run) {
    Write-Host "starting..." -ForegroundColor Cyan
    Start-Process -FilePath $OutputPath
}
