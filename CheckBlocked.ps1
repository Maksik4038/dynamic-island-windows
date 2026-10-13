<#
  Finds out whether Windows security is blocking DynamicIsland.exe.

      powershell -ExecutionPolicy Bypass -File CheckBlocked.ps1

  Reads settings and the event log. Nothing is changed unless you pass -Fix,
  and even then it only adds a Defender exclusion for this folder.

  Written in plain ASCII on purpose: Windows PowerShell 5.1 reads a .ps1 without
  a byte-order mark as ANSI, which mangles non-Latin text and breaks parsing.
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $here 'DynamicIsland.exe'

function Line  { param($t) Write-Host ""; Write-Host $t -ForegroundColor Cyan }
function Ok    { param($t) Write-Host "  [  OK  ]  $t" -ForegroundColor Green }
function Block { param($t) Write-Host "  [BLOCK]  $t" -ForegroundColor Red }
function Maybe { param($t) Write-Host "  [  ?   ]  $t" -ForegroundColor Yellow }
function Note  { param($t) Write-Host "            $t" -ForegroundColor DarkGray }

Write-Host ""
Write-Host "Dynamic Island - Windows security check" -ForegroundColor White
Write-Host ("=" * 64)

# ---------------------------------------------------------------- 1. Smart App Control
Line "1. Smart App Control  (Intellektualnoe upravlenie)"
$sac = $null
try {
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'
    if (Test-Path $key) { $sac = (Get-ItemProperty $key -ErrorAction Stop).VerifiedAndReputablePolicyState }
} catch { }

if ($sac -eq 0) {
    Ok "OFF - this protection is not blocking anything"
} elseif ($sac -eq 1) {
    Block "ON in enforcing mode"
    Note "This is what stops the program. Smart App Control refuses to run any"
    Note "executable that is not signed by a known publisher, and there is no"
    Note "per-app exception. See the options at the end."
} elseif ($sac -eq 2) {
    Maybe "ON in evaluation mode - it blocks only what it considers unsafe"
    Note "It can still block this file. Check the event log in section 3."
} else {
    Maybe "state could not be read (value: $sac)"
}

# ---------------------------------------------------------------- 2. SmartScreen / MOTW
Line "2. SmartScreen and the 'downloaded from the internet' mark"
try {
    $p = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' -ErrorAction Stop
    if ($p.SmartScreenEnabled) { Ok ("SmartScreenEnabled = " + $p.SmartScreenEnabled) }
    else { Ok "SmartScreen is not configured for Explorer" }
} catch { Maybe "SmartScreen setting could not be read" }

if (Test-Path -LiteralPath $exe) {
    $streams = Get-Item -LiteralPath $exe -Stream * -ErrorAction SilentlyContinue
    $zone = $streams | Where-Object { $_.Stream -eq 'Zone.Identifier' }
    if ($zone) {
        Maybe "the file carries the 'from the internet' mark - SmartScreen will warn"
        Note "Clear it with:  Unblock-File '$exe'"
    } else {
        Ok "no 'from the internet' mark on the file"
    }
} else {
    Block "DynamicIsland.exe is not next to this script"
}

# ---------------------------------------------------------------- 3. event log
Line "3. Event log - who actually blocked it"
# Only the events that mean "this file was refused" count. The CodeIntegrity log
# is noisy: policy refreshes (3099) and signature notes (3116) happen constantly
# and are not blocks at all.
$blockIds = @(3033, 3077, 3023, 3118)
$found = $false
try {
    $ev = Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-CodeIntegrity/Operational'
        Id        = $blockIds
        StartTime = (Get-Date).AddDays(-7)
    } -MaxEvents 20 -ErrorAction SilentlyContinue
    if ($ev) {
        foreach ($e in $ev) {
            $first = ($e.Message -split "`r?`n")[0]
            Write-Host ("  {0}  id={1}  {2}" -f $e.TimeCreated.ToString('dd.MM HH:mm'), $e.Id, $first) -ForegroundColor DarkGray
        }
        Block "code integrity refused a file in the last seven days (see ids above)"
        $found = $true
    }
} catch { }

if (-not $found) {
    Ok "no code-integrity block events in the last seven days"
    try {
        $def = Get-WinEvent -LogName 'Microsoft-Windows-Windows Defender/Operational' -MaxEvents 40 -ErrorAction SilentlyContinue |
               Where-Object { $_.Id -eq 1116 -or $_.Id -eq 1117 }
        if ($def) {
            foreach ($e in $def) { Write-Host ("  Defender {0}  id={1}" -f $e.TimeCreated.ToString('dd.MM HH:mm'), $e.Id) -ForegroundColor DarkGray }
            Block "Defender logged detections - check the quarantine"
        } else {
            Ok "Defender logged no detections either"
        }
    } catch { Maybe "Defender log could not be read" }
}

# ---------------------------------------------------------------- 4. Defender
Line "4. Microsoft Defender"
try {
    $st = Get-MpComputerStatus -ErrorAction Stop
    Ok ("real-time protection: " + $st.RealTimeProtectionEnabled + ", antivirus: " + $st.AMServiceEnabled)
    $det = Get-MpThreatDetection -ErrorAction SilentlyContinue |
           Where-Object { $_.Resources -like '*DynamicIsland*' }
    if ($det) { Block "Defender flagged DynamicIsland files" } else { Ok "DynamicIsland is not in Defender detections" }
} catch { Maybe "Defender state could not be read" }

# ---------------------------------------------------------------- 5. try to run
Line "5. Test run"
if (Test-Path -LiteralPath $exe) {
    $out = Join-Path $env:TEMP ("di_check_{0}.txt" -f (Get-Random))
    try {
        $pr = Start-Process -FilePath $exe -ArgumentList '--selftest' -NoNewWindow -PassThru `
                            -RedirectStandardOutput $out -RedirectStandardError "$out.err" -ErrorAction Stop
        if (-not $pr.WaitForExit(25000)) { $pr.Kill(); Maybe "the self test did not finish in 25 s" }
        if (Test-Path -LiteralPath $out) {
            if (Select-String -Path $out -Pattern 'SELFTEST PASS' -Quiet) {
                Ok "the program started and passed its self test"
            } else {
                Block "it started but the self test did not pass:"
                Get-Content $out | ForEach-Object { Note $_ }
            }
        }
    } catch {
        Block "starting it failed: $($_.Exception.Message)"
        Note "That is the Windows block."
    } finally {
        Remove-Item -LiteralPath $out, "$out.err" -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- 6. options
Line "What to do"
Write-Host @"
  Try these in order, simplest first.

  A. Section 1 says 1 (ON) - three options:

     A1. Build the program on this same computer from the sources.
         Windows treats freshly compiled code more leniently than downloaded
         code. You need the Windows SDK 10, but no administrator rights:
             powershell -ExecutionPolicy Bypass -File Build.ps1
         If the exe it produces starts, use that one.

     A2. Turn Smart App Control off.
         Settings -> Privacy and security -> Windows Security
         -> App and browser control -> Smart App Control.
         WARNING: it cannot be turned back on without reinstalling Windows.
         Windows will warn you about this itself.

     A3. Check whether Defender got in the way (section 4).

  B. Section 1 says 2 (evaluation) - usually enough to clear the mark:
         Unblock-File '$exe'
     then start it again.

  C. Defender blocked it - add this folder to the exclusions:
         Add-MpPreference -ExclusionPath '$here'
     or run this script with -Fix to do exactly that.

  D. The only way to make it start for ANY user without a warning is to sign it
     with a publisher certificate. A real one costs money and needs an identity
     check; a self-signed one does not help because nothing trusts it by default.
"@ -ForegroundColor DarkGray

if ($Fix) {
    Line "Adding a Defender exclusion"
    try {
        Add-MpPreference -ExclusionPath $here -ErrorAction Stop
        Ok "folder excluded: $here"
    } catch {
        Block "could not add the exclusion: $($_.Exception.Message)"
    }
}

Write-Host ""
Read-Host "Press Enter to close"
