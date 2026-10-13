<#
  Diagnoses why Dynamic Island will not start on this machine.

  Run it in the folder that contains DynamicIsland.exe:

      powershell -ExecutionPolicy Bypass -File Diagnose.ps1

  It only reads. Nothing is installed or changed.
#>
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ok = 0; $bad = 0; $warn = 0

function Good($m) { Write-Host "  [ OK ]  $m" -ForegroundColor Green; $script:ok++ }
function Fail($m) { Write-Host "  [FAIL]  $m" -ForegroundColor Red; $script:bad++ }
function Warn($m) { Write-Host "  [WARN]  $m" -ForegroundColor Yellow; $script:warn++ }

Write-Host ""
Write-Host "Dynamic Island - diagnostic" -ForegroundColor Cyan
Write-Host ("=" * 58)

# ---------------------------------------------------------------- OS
Write-Host "`n1. Windows"
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Good ("{0} (build {1})" -f $os.Caption, $os.BuildNumber)
    if ([int]$os.BuildNumber -lt 22000) {
        Warn "Island is built for Windows 11. On Windows 10 it may not appear."
    }
} catch { Warn "could not read the OS version" }

$arch = $env:PROCESSOR_ARCHITECTURE
Good "architecture: $arch"

# ---------------------------------------------------------------- .NET
Write-Host "`n2. .NET Framework"
$ndp = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$rel = (Get-ItemProperty $ndp -ErrorAction SilentlyContinue).Release
if (-not $rel) {
    Fail ".NET Framework 4.x not found - install it, nothing will run without it"
} else {
    # 528040 = 4.8, 533320 = 4.8.1
    $ver = (Get-ItemProperty $ndp -ErrorAction SilentlyContinue).Version
    if ($rel -ge 528040) { Good ".NET Framework $ver (release $rel)" }
    else { Warn ".NET Framework $ver (release $rel) - 4.8 or newer recommended" }
}

# ---------------------------------------------------------------- exe
Write-Host "`n3. The executable"
$exe = Join-Path $here 'DynamicIsland.exe'
if (Test-Path -LiteralPath $exe) {
    $size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1KB, 1)
    Good "found DynamicIsland.exe ($size KB)"
    $z = Get-Item -LiteralPath $exe -Stream * -ErrorAction SilentlyContinue |
         Where-Object { $_.Stream -eq 'Zone.Identifier' }
    if ($z) { Warn "file is marked as downloaded from the internet; run: Unblock-File '$exe'" }
} else {
    Fail "DynamicIsland.exe is missing from $here"
    Write-Host "         It is not stored in the repository (build output is ignored)." -ForegroundColor Gray
    Write-Host "         Build it first:" -ForegroundColor Gray
    Write-Host "             powershell -ExecutionPolicy Bypass -File Build.ps1" -ForegroundColor Gray
}

# ---------------------------------------------------------------- build tools
Write-Host "`n4. Build tools"
$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($csc) { Good "compiler: $csc" } else { Fail "csc.exe not found - .NET Framework is incomplete" }

$winmd = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata",
    "$env:ProgramFiles\Windows Kits\10\UnionMetadata"
) | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory -ErrorAction SilentlyContinue } |
    ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($winmd) { Good "Windows SDK: $(Split-Path $winmd -Parent | Split-Path -Leaf)" }
else { Warn "Windows.winmd not found - needed only to COMPILE, not to run (install the Windows SDK 10)" }

# ---------------------------------------------------------------- previous crash
Write-Host "`n5. Previous errors"
$log = Join-Path $here 'island-error.log'
if (Test-Path -LiteralPath $log) {
    Fail "the app crashed earlier - see island-error.log below"
    Write-Host ""
    Get-Content -LiteralPath $log -Tail 20 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
} else {
    Good "no island-error.log - the app has not reported a startup crash here"
}

# ---------------------------------------------------------------- tray / shell
Write-Host "`n6. Desktop shell"
$explorer = Get-Process explorer -ErrorAction SilentlyContinue
if ($explorer) { Good "explorer.exe is running" } else { Warn "explorer.exe is not running - the tray icon will not appear" }

# ---------------------------------------------------------------- live probes
Write-Host "`n7. Running the built-in self test"
if (Test-Path -LiteralPath $exe) {
    $out = Join-Path $env:TEMP ("di_selftest_{0}.txt" -f (Get-Random))
    try {
        $p = Start-Process -FilePath $exe -ArgumentList '--selftest' -NoNewWindow -PassThru `
                           -RedirectStandardOutput $out -RedirectStandardError "$out.err" -ErrorAction Stop
        if (-not $p.WaitForExit(20000)) { $p.Kill(); Warn "self test did not finish within 20 s" }
        if (Test-Path -LiteralPath $out) {
            Get-Content -LiteralPath $out | ForEach-Object { Write-Host "         $_" -ForegroundColor Gray }
        }
        if ((Test-Path -LiteralPath "$out.err") -and (Get-Item -LiteralPath "$out.err").Length -gt 0) {
            Write-Host "         stderr:" -ForegroundColor DarkGray
            Get-Content -LiteralPath "$out.err" | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
        }
        $p.Refresh()
        $code = $p.ExitCode
        if ($code -eq 0) { Good "self test finished cleanly" }
        elseif ($null -eq $code) { Warn "self test produced no exit code - read the lines above" }
        else { Warn "self test exit code: $code - the lines above say what failed" }
    } catch {
        Fail "could not start DynamicIsland.exe --selftest : $($_.Exception.Message)"
    } finally {
        Remove-Item -LiteralPath $out, "$out.err" -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- summary
Write-Host ""
Write-Host ("=" * 58)
Write-Host ("  OK: $ok    warnings: $warn    problems: $bad") -ForegroundColor Cyan
Write-Host ""
if ($bad -gt 0) {
    Write-Host "  Fix the [FAIL] lines above first." -ForegroundColor Red
} elseif ($warn -gt 0) {
    Write-Host "  No blocking problems found. If the island still does not appear," -ForegroundColor Yellow
    Write-Host "  start it from a terminal to see errors live:" -ForegroundColor Yellow
    Write-Host "      .\DynamicIsland.exe" -ForegroundColor Gray
} else {
    Write-Host "  Everything checks out. The island should start normally." -ForegroundColor Green
    Write-Host "      .\DynamicIsland.exe" -ForegroundColor Gray
}
Write-Host ""
Read-Host "Press Enter to close"
