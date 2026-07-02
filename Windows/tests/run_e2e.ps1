# run_e2e.ps1 - Elevated e2e test runner for full-cone UDP tests
# Usage: run_e2e.ps1 -ProfilePath <path> -LogFile <path>
# Must run as Administrator (WinDivert requires kernel access).

param(
    [Parameter(Mandatory=$true)]
    [string]$ProfilePath,

    [Parameter(Mandatory=$true)]
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
$output = [System.Collections.Generic.List[string]]::new()

function Log([string]$msg) {
    $ts = (Get-Date -Format 'HH:mm:ss')
    $line = "[$ts] $msg"
    Write-Host $line
    $output.Add($line)
}

function Flush-Log {
    $output | Set-Content -Path $LogFile -Encoding UTF8
}

Log "=== ProxyBridge Full-Cone UDP E2E Test ==="
Log "Profile : $ProfilePath"
Log "Log file: $LogFile"

# Locate xray.exe
$xrayExe = $null
$settingsPath = "$env:APPDATA\ProxyBridgeXRay\settings.json"
if (Test-Path $settingsPath) {
    try {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($settings.XRayPath -and (Test-Path $settings.XRayPath)) {
            $xrayExe = $settings.XRayPath
        }
    } catch {}
}
if (-not $xrayExe) {
    $candidates = @(
        "C:\Program Files\ProxyBridgeXRay\xray.exe",
        "C:\Program Files\ProxyBridgeXray\xray.exe",
        "$env:LOCALAPPDATA\ProxyBridgeXRay\xray.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $xrayExe = $c; break }
    }
}
if (-not $xrayExe) {
    $xrayCmd = Get-Command xray -ErrorAction SilentlyContinue
    if ($xrayCmd) { $xrayExe = $xrayCmd.Source }
}
if (-not $xrayExe) {
    Log "ERROR: Cannot find xray.exe. Check settings or install ProxyBridgeXRay."
    Flush-Log
    exit 2
}
Log "xray.exe : $xrayExe"

# Locate CLI
$scriptDir  = Split-Path $MyInvocation.MyCommand.Path -Parent
$repoRoot   = (Resolve-Path (Join-Path $scriptDir '..\..'))
$outputDir  = Join-Path $repoRoot 'Windows\output'
$cliExe     = Join-Path $outputDir 'ProxyBridgeXRay_CLI.exe'
if (-not (Test-Path $cliExe)) {
    Log "ERROR: CLI not found at $cliExe — run compile.ps1 first."
    Flush-Log
    exit 2
}
Log "CLI      : $cliExe"

# xray config
$xrayCfg = Join-Path $scriptDir 'xray-local-socks.json'

# Start xray
Log "Starting xray..."
$xrayProc = Start-Process -FilePath $xrayExe -ArgumentList "run -c `"$xrayCfg`"" `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\xray_stdout.txt" `
    -RedirectStandardError "$env:TEMP\xray_stderr.txt"
Log "xray PID : $($xrayProc.Id)"

# Start CLI
Log "Starting CLI..."
$cliProc = Start-Process -FilePath $cliExe `
    -ArgumentList "--profile `"$ProfilePath`" --verbose 3" `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$env:TEMP\cli_stdout.txt" `
    -RedirectStandardError  "$env:TEMP\cli_stderr.txt"
Log "CLI PID  : $($cliProc.Id)"

# Wait for UDP relay to come up
Log "Waiting 3 s for relay to initialise..."
Start-Sleep -Seconds 3

# Run the Python test
Log "Running fullcone_udp_test.py..."
$testScript = Join-Path $scriptDir 'fullcone_udp_test.py'
$pyProc = Start-Process -FilePath 'python' -ArgumentList "`"$testScript`"" `
    -PassThru -Wait -WindowStyle Hidden `
    -RedirectStandardOutput "$env:TEMP\py_stdout.txt" `
    -RedirectStandardError  "$env:TEMP\py_stderr.txt"
$pyExit = $pyProc.ExitCode

# Collect python output
if (Test-Path "$env:TEMP\py_stdout.txt") {
    Get-Content "$env:TEMP\py_stdout.txt" | ForEach-Object { Log "[PY] $_" }
}
if (Test-Path "$env:TEMP\py_stderr.txt") {
    Get-Content "$env:TEMP\py_stderr.txt" | ForEach-Object { Log "[PY-ERR] $_" }
}

Log "Python exit code: $pyExit"

# Collect CLI output
if (Test-Path "$env:TEMP\cli_stdout.txt") {
    Get-Content "$env:TEMP\cli_stdout.txt" | ForEach-Object { Log "[CLI] $_" }
}
if (Test-Path "$env:TEMP\cli_stderr.txt") {
    Get-Content "$env:TEMP\cli_stderr.txt" | ForEach-Object { Log "[CLI-ERR] $_" }
}

# Stop CLI and xray
Log "Stopping CLI..."
try { Stop-Process -Id $cliProc.Id -Force -ErrorAction SilentlyContinue } catch {}
Log "Stopping xray..."
try { Stop-Process -Id $xrayProc.Id -Force -ErrorAction SilentlyContinue } catch {}

# Collect xray output
if (Test-Path "$env:TEMP\xray_stderr.txt") {
    Get-Content "$env:TEMP\xray_stderr.txt" | ForEach-Object { Log "[XRAY] $_" }
}

Log "=== TEST RESULT: $(if ($pyExit -eq 0) { 'PASS' } else { 'FAIL' }) (exit $pyExit) ==="
Flush-Log
exit $pyExit
