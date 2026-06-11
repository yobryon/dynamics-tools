#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Smoke test for the XppService gRPC server.

.DESCRIPTION
    Spawns the service in the background, waits for the named pipe to be
    listening, runs the XppService.PingProbe against it, asserts success,
    then signals the service to shut down. Verifies that the full path
    (gRPC client -> service -> bridge child process -> bridge ping ->
    back) is working end-to-end.

    All build steps go through dotnet directly; this script doesn't depend
    on the run-service action in dev.ps1.

    Exit codes:
      0  service started, probe succeeded, clean shutdown
      1  any assertion failed
      2  could not start service or probe
#>

[CmdletBinding()]
param(
    [string]$PipeName = 'xpp-service-v2-test',
    # A small custom model present on this dev VM, used by the rebuild probe.
    # Supply your own (or set $env:XPP_TEST_MODEL); the rebuild probe is
    # skipped when this is empty.
    [string]$Model    = $env:XPP_TEST_MODEL
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
$repoRoot  = Split-Path -Parent $scriptDir

function Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red; $script:failed = $true }
function Pass([string]$msg) { Write-Host "[PASS] $msg" -ForegroundColor Green }

$script:failed = $false

# Build everything fresh so the test always reflects current sources.
Write-Host '=== Building solution ===' -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'dynamics-xpp.sln') --nologo -v minimal | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host '[ERROR] Build failed.' -ForegroundColor Red
    exit 2
}

$serviceExe = Join-Path $repoRoot 'src/XppService/bin/Debug/net9.0-windows/XppService.exe'
$probeExe   = Join-Path $repoRoot 'src/XppService.PingProbe/bin/Debug/net9.0/XppService.PingProbe.exe'
$bridgeExe  = Join-Path $repoRoot 'src/XppMetadataBridge/bin/Debug/net48/XppMetadataBridge.exe'

foreach ($p in @($serviceExe, $probeExe, $bridgeExe)) {
    if (-not (Test-Path $p)) { Write-Host "[ERROR] Missing artifact: $p" -ForegroundColor Red; exit 2 }
}

Write-Host '=== Starting XppService ===' -ForegroundColor Cyan
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $serviceExe
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.CreateNoWindow = $true
# Use a test-specific pipe name to avoid stepping on any real service
# already running on the box. Configuration override via env var follows
# the ASP.NET Core convention (Section__Key).
$psi.EnvironmentVariables['XppService__PipeName']        = $PipeName
$psi.EnvironmentVariables['XppService__BridgeExecutable'] = $bridgeExe

# Use a per-run temp directory so the test never collides with a real
# cache. Cleaned up in the finally block.
$dataDir = Join-Path ([System.IO.Path]::GetTempPath()) "xpp-test-$(Get-Random)"
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
$psi.EnvironmentVariables['XppService__DataDirectory'] = $dataDir

# Pass through the D365 paths so the service can hand them to the bridge.
# The rebuild test needs working metadata access; ping/status work either way.
$defaultPackages = 'J:\AosService\PackagesLocalDirectory'
if (Test-Path $defaultPackages) {
    $psi.EnvironmentVariables['D365__PackagesLocalDirectory'] = $defaultPackages
    $psi.EnvironmentVariables['D365__CustomMetadataPath']     = $defaultPackages
}

$service = [System.Diagnostics.Process]::Start($psi)
if (-not $service) { Write-Host '[ERROR] Failed to start XppService' -ForegroundColor Red; exit 2 }

try {
    # Wait for the named pipe to appear. We poll for up to 10s; Kestrel
    # binding is usually sub-second but the bridge startup probe adds ~250ms.
    $deadline = (Get-Date).AddSeconds(10)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Path "\\.\pipe\$PipeName") {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
        if ($service.HasExited) {
            Write-Host '[ERROR] Service exited before pipe became available.' -ForegroundColor Red
            $stdout = $service.StandardOutput.ReadToEnd()
            $stderr = $service.StandardError.ReadToEnd()
            if ($stdout) { Write-Host "stdout:`n$stdout" }
            if ($stderr) { Write-Host "stderr:`n$stderr" }
            exit 2
        }
    }

    if (-not $ready) {
        Fail "service did not open pipe $PipeName within 10s"
    } else {
        Pass "service listening on pipe $PipeName"
    }

    if (-not $script:failed) {
        Write-Host '=== Running PingProbe ===' -ForegroundColor Cyan
        $probeOut = & $probeExe $PipeName "smoke-test-$(Get-Random)"
        $probeExit = $LASTEXITCODE
        $probeOut | ForEach-Object { Write-Host "  probe> $_" -ForegroundColor DarkGray }

        if ($probeExit -ne 0) {
            Fail "probe exited with code $probeExit"
        } else {
            Pass 'probe round-tripped through service and bridge'
        }
    }

    # --- Rebuild probe (gated on D365 + a small custom model being present) -
    # Pick a small CUSTOM model (low object count -> fast test; not a Microsoft
    # module so we don't get tangled in package-vs-model semantics). Pass it via
    # -Model or $env:XPP_TEST_MODEL. If unset (or D365 isn't installed) we skip
    # rather than fail; the rest of the smoke test still proves the gRPC path.
    if (-not $script:failed -and $Model -and (Test-Path 'J:\AosService\PackagesLocalDirectory')) {
        Write-Host "=== Running RebuildIndex probe (model: $Model) ===" -ForegroundColor Cyan
        $rebuildOut = & $probeExe '--rebuild' $Model $PipeName
        $rebuildExit = $LASTEXITCODE
        $rebuildOut | ForEach-Object { Write-Host "  probe> $_" -ForegroundColor DarkGray }
        if ($rebuildExit -ne 0) {
            Fail "rebuild probe exited with code $rebuildExit"
        } else {
            Pass 'rebuild probe streamed progress and saw object count grow'
        }
    } else {
        Write-Host '[SKIP] rebuild probe: PackagesLocalDirectory not present' -ForegroundColor DarkGray
    }

} finally {
    Write-Host '=== Shutting down XppService ===' -ForegroundColor Cyan

    # PS 5.1's .NET Framework Process class lacks the Kill(bool) overload
    # that kills the whole tree, so kill the service and any orphaned bridge
    # children manually. Track bridge pids before we kill the service so we
    # don't accidentally take out an unrelated XppMetadataBridge.exe.
    $bridgePidsToKill = @()
    if (-not $service.HasExited) {
        try {
            $bridgePidsToKill = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($service.Id)" |
                Where-Object { $_.Name -eq 'XppMetadataBridge.exe' } |
                Select-Object -ExpandProperty ProcessId
        } catch { }

        try { $service.Kill() } catch { }
        try { $service.WaitForExit(5000) | Out-Null } catch { }
    }

    foreach ($bpid in $bridgePidsToKill) {
        try { Stop-Process -Id $bpid -Force -ErrorAction SilentlyContinue } catch { }
    }

    if ($service.HasExited) {
        Pass "service shut down (exit $($service.ExitCode))"
    } else {
        Fail 'service did not exit within 5s of kill signal'
    }

    # Surface any service-side logs that would help debugging on failure.
    if ($script:failed) {
        $stdout = $service.StandardOutput.ReadToEnd()
        $stderr = $service.StandardError.ReadToEnd()
        if ($stdout) { Write-Host "service stdout:`n$stdout" -ForegroundColor DarkGray }
        if ($stderr) { Write-Host "service stderr:`n$stderr" -ForegroundColor DarkGray }
    }

    # Clean up the per-run data directory.
    if ($dataDir -and (Test-Path $dataDir)) {
        try { Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue } catch { }
    }
}

if ($script:failed) {
    Write-Host ''
    Write-Host 'Service smoke test FAILED.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Service smoke test passed.' -ForegroundColor Green
exit 0
