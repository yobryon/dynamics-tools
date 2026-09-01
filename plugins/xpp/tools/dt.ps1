<#
.SYNOPSIS
    dt - the dynamics-xpp user CLI.

.DESCRIPTION
    Day-to-day commands for people USING the plugin: get it set up, keep it
    updated, see what the service is doing, and recover when something is
    stuck. Maintainer actions (build one project, run a component in the
    foreground, smoke tests) stay in dev.ps1 -- this is deliberately the
    smaller, safer surface.

    Windows-only by design: the plugin only runs against a local D365 dev box.

    Commands:
      dt setup              One-time per machine. Locates the D365 metadata
                            assemblies, writes the per-machine references file,
                            builds, and offers to put dt on your PATH.
      dt update             Updates the marketplace + plugin, then rebuilds.
      dt version            Installed plugin version, and the version of the
                            service actually running.
      dt status             Live service + index status.
      dt service status     Same as 'dt status'.
      dt service stop       Asks the service to stop gracefully.
      dt service restart    Stops it, then starts the current build.
      dt cache clear        Deletes the index cache. Costs a full re-index.
      dt help               This text.

.EXAMPLE
    dt setup
    dt status
    dt service restart
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$SubCommand,

    # Skip confirmation prompts. For 'cache clear', which is destructive.
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$PluginRoot = Split-Path -Parent $PSScriptRoot
$PipeName   = 'xpp-service-v2'

$ProbeExe   = Join-Path $PluginRoot 'src\XppService.PingProbe\bin\Release\net10.0\XppService.PingProbe.exe'
$ServiceExe = Join-Path $PluginRoot 'src\XppService\bin\Release\net10.0-windows\XppService.exe'
$SolutionFile = Join-Path $PluginRoot 'dynamics-xpp.sln'
$DevScript  = Join-Path $PSScriptRoot 'dev.ps1'

# Probe exit code for "nothing is listening on the pipe" -- an ordinary state,
# not a failure. Keep in sync with XppService.PingProbe.
$EXIT_NOT_RUNNING = 3

<#
    Establish CLAUDE_PLUGIN_DATA before anything builds.

    The bridge csproj imports BridgeReferences.props from $(CLAUDE_PLUGIN_DATA)
    when that is set, and from next to the csproj when it isn't. Claude Code
    sets it; the plain shell dt runs in does not. So EVERY command that builds
    has to establish it -- not just setup. Getting that wrong is exactly how
    'dt setup' could succeed and 'dt update' then fail to find the very props
    file setup had just written.

    Only inferred for a marketplace clone, where the layout is a known
    convention. From a dev tree we leave it unset so the file stays next to the
    csproj, which is where dev builds expect it.
#>
function Initialize-PluginDataDir {
    # An explicit value always wins: if Claude Code (or the user) set it, that
    # is the answer.
    if ($env:CLAUDE_PLUGIN_DATA) { return }

    # Locate the marketplace segment by splitting the path rather than by
    # regex: that pattern needs a pile of escaped backslashes, which is easy
    # to get subtly wrong and then fails at RUNTIME, inside a command the user
    # is already running. Plain string work has no such trap.
    $marker = '.claude\plugins\marketplaces\'
    $idx = $PluginRoot.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
    if ($idx -lt 0) { return }
    $rest = $PluginRoot.Substring($idx + $marker.Length)
    $marketplace = ($rest -split '\\')[0]
    if ([string]::IsNullOrWhiteSpace($marketplace)) { return }

    $pluginName  = Split-Path $PluginRoot -Leaf
    $dataDir     = Join-Path $env:USERPROFILE ".claude\plugins\data\$pluginName-$marketplace"

    # Don't strand an existing setup: if a props file already sits next to the
    # csproj and the data dir has none, keep using the one that exists.
    $adjacent = Join-Path $PluginRoot 'src\XppMetadataBridge\BridgeReferences.props'
    $inData   = Join-Path $dataDir 'BridgeReferences.props'
    if ((Test-Path $adjacent) -and -not (Test-Path $inData)) { return }

    $env:CLAUDE_PLUGIN_DATA = $dataDir
}

function Write-Head { param([string]$m) Write-Host $m -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "  $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "  $m" -ForegroundColor Yellow }
function Write-Err  { param([string]$m) Write-Host "  $m" -ForegroundColor Red }
function Write-Dim  { param([string]$m) Write-Host "  $m" -ForegroundColor DarkGray }

function Confirm-Action {
    param([string]$Question, [switch]$DefaultYes)
    if ($Yes) { return $true }
    $suffix = if ($DefaultYes) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$Question $suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return [bool]$DefaultYes }
    return $answer -match '^(y|yes)$'
}

<#
    The installed plugin version, read from plugin.json -- the same single
    source of truth the assemblies are stamped from (Directory.Build.props), so
    "the version dt reports" and "the version the service reports" are the same
    number by construction.
#>
function Get-PluginVersion {
    $pluginJson = Join-Path $PluginRoot '.claude-plugin\plugin.json'
    if (-not (Test-Path $pluginJson)) { return $null }
    try { return (Get-Content $pluginJson -Raw | ConvertFrom-Json).version }
    catch { return $null }
}

<#
    Ask the running service something, via the PingProbe.

    We shell out rather than talking gRPC from PowerShell on purpose: the
    transport is HTTP/2 over a named pipe with a custom connect callback, which
    is bespoke to reimplement, and the net10 client assemblies can't be loaded
    into Windows PowerShell 5.1 at all. The probe is contract-only (no bridge
    dependencies), so it builds on any box without the D365 tooling located.

    Returns a hashtable: Running, Data (parsed JSON, may be $null), Available
    (false when the probe binary hasn't been built yet).
#>
function Invoke-Probe {
    param([ValidateSet('status', 'shutdown')][string]$Mode = 'status')

    if (-not (Test-Path $ProbeExe)) {
        return @{ Available = $false; Running = $false; Data = $null }
    }

    # stderr carries diagnostics only; stdout is the single JSON line.
    $out = & $ProbeExe "--$Mode" $PipeName 2>$null
    $code = $LASTEXITCODE

    $data = $null
    if ($out) {
        try { $data = ($out | Select-Object -Last 1) | ConvertFrom-Json } catch { $data = $null }
    }

    return @{
        Available = $true
        Running   = ($code -ne $EXIT_NOT_RUNNING)
        ExitCode  = $code
        Data      = $data
    }
}

function Show-NotBuilt {
    Write-Warn 'The service tools have not been built on this machine yet.'
    Write-Dim  'Run:  dt setup'
}

function Get-ServiceProcesses {
    Get-Process -Name XppService -ErrorAction SilentlyContinue
}

<#
    Stop the service gracefully: the same RequestShutdown RPC the newest-wins
    takeover uses, so in-flight calls drain and the index is checkpointed
    rather than killed mid-write. Falls back to Stop-Process only if the
    cooperative path doesn't get there -- and says so.
#>
function Stop-XppService {
    param([int]$TimeoutSeconds = 30)

    $probe = Invoke-Probe -Mode shutdown
    if (-not $probe.Available) { Show-NotBuilt; return $false }

    if (-not $probe.Running) {
        Write-Dim 'Service is not running.'
        return $true
    }

    # NOT $pid -- that's a read-only PowerShell automatic variable.
    $svcPid = 0
    if ($probe.Data) { $svcPid = [int]$probe.Data.processId }
    Write-Ok "Shutdown accepted by pid $svcPid; draining..."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $svcPid -ErrorAction SilentlyContinue)) {
            Write-Ok 'Service stopped.'
            return $true
        }
        Start-Sleep -Milliseconds 250
    }

    Write-Warn "Service did not exit within ${TimeoutSeconds}s; forcing."
    Get-ServiceProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name XppMetadataBridge -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    return $true
}

function Start-XppService {
    if (-not (Test-Path $ServiceExe)) { Show-NotBuilt; return $false }

    Start-Process -FilePath $ServiceExe `
                  -WorkingDirectory (Split-Path $ServiceExe) `
                  -WindowStyle Hidden | Out-Null

    # Wait for the pipe rather than declaring victory on Process.Start: a
    # schema-downgrade refusal (exit 78) also "starts" successfully.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $probe = Invoke-Probe -Mode status
        if ($probe.Running) {
            $v = if ($probe.Data) { $probe.Data.pluginVersion } else { '?' }
            Write-Ok "Service is up (plugin $v)."
            return $true
        }
        if (-not (Get-ServiceProcesses)) {
            Write-Err 'The service exited during startup.'
            Write-Dim "Run it in the foreground to see why:  $ServiceExe"
            return $false
        }
        Start-Sleep -Milliseconds 500
    }

    Write-Warn 'Service did not open its pipe within 60s.'
    return $false
}

function Format-Count {
    param($n)
    if ($null -eq $n) { return '-' }
    return ('{0:N0}' -f [int64]$n)
}

# =============================================================================
# Commands
# =============================================================================

function Cmd-Help {
    Write-Host ''
    Write-Head 'dt - dynamics-xpp CLI'
    Write-Host ''
    Write-Host '  dt setup             One-time machine setup: locate D365 assemblies, build,'
    Write-Host '                       and offer to put dt on your PATH.'
    Write-Host '  dt update            Update the marketplace + plugin, then rebuild.'
    Write-Host '  dt version           Installed plugin version vs. the running service.'
    Write-Host '  dt status            Live service and index status.'
    Write-Host ''
    Write-Host '  dt service status    Same as dt status.'
    Write-Host '  dt service stop      Ask the service to stop gracefully.'
    Write-Host '  dt service restart   Stop it, then start the current build.'
    Write-Host ''
    Write-Host '  dt cache clear       Delete the index cache (costs a full re-index).'
    Write-Host '  dt help              This text.'
    Write-Host ''
    Write-Dim  'Maintainer actions (build, run-service, test, clean) live in tools/dev.ps1.'
    Write-Host ''
}

function Cmd-Version {
    Write-Host ''
    Write-Head 'dynamics-xpp version'
    Write-Host ''

    $installed = Get-PluginVersion
    if ($installed) { Write-Host "  installed plugin : $installed" }
    else            { Write-Warn 'installed plugin : (could not read plugin.json)' }

    $probe = Invoke-Probe -Mode status
    if (-not $probe.Available) {
        Write-Dim 'running service  : (tools not built - run: dt setup)'
    } elseif (-not $probe.Running) {
        Write-Dim 'running service  : not running'
    } else {
        $running = $probe.Data.pluginVersion
        Write-Host "  running service  : $running (pid $($probe.Data.processId))"

        # The newest-wins negotiation means a mismatch is self-correcting on
        # the next session start -- so this is information, not an error.
        if ($installed -and $running -and ($installed -ne $running)) {
            Write-Host ''
            Write-Warn "The running service is a different build than the installed plugin."
            Write-Dim  'The next Claude Code session will take over automatically.'
            Write-Dim  'To do it now:  dt service restart'
        }
    }
    Write-Host ''
}

function Cmd-Status {
    Write-Host ''
    Write-Head 'dynamics-xpp status'
    Write-Host ''

    $probe = Invoke-Probe -Mode status
    if (-not $probe.Available) { Show-NotBuilt; Write-Host ''; return }
    if (-not $probe.Running) {
        Write-Dim 'Service is not running.'
        Write-Dim 'It starts automatically with your next Claude Code session, or:  dt service restart'
        Write-Host ''
        return
    }

    $d = $probe.Data
    if (-not $d) {
        Write-Err 'The service answered but the response could not be parsed.'
        Write-Host ''
        return
    }

    Write-Host "  service        : plugin $($d.pluginVersion), pid $($d.processId)"
    if ($d.bridgeHealthy) { Write-Ok   'bridge         : healthy' }
    else                  { Write-Err  'bridge         : NOT healthy' }

    Write-Host "  index          : $($d.indexState)$(if ($d.sweepInProgress) { ' (sweep in progress)' })"
    Write-Host "  objects        : $(Format-Count $d.objectCount)"
    Write-Host "  methods        : $(Format-Count $d.methodCount)"
    Write-Host "  labels         : $(Format-Count $d.labelCount)"
    Write-Host "  references     : $(Format-Count $d.referenceCount)"

    if ($d.embeddingState -and $d.embeddingState -ne 'off') {
        $pct = ''
        if ($d.embeddingTotal -gt 0) {
            $pct = ' ({0:N1}%)' -f (100.0 * $d.embeddingCount / $d.embeddingTotal)
        }
        Write-Host "  embeddings     : $($d.embeddingState) - $(Format-Count $d.embeddingCount) / $(Format-Count $d.embeddingTotal)$pct"
    } else {
        Write-Dim 'embeddings     : off (semantic search disabled)'
    }

    if ($d.lastSweepAt)     { Write-Dim "last sweep     : $($d.lastSweepAt)" }
    if ($d.lastIndexUpdate) { Write-Dim "last update    : $($d.lastIndexUpdate)" }
    Write-Host ''
}

function Cmd-Service {
    param([string]$Action)

    switch ($Action) {
        'status'  { Cmd-Status }
        'stop'    {
            Write-Host ''
            Write-Head 'Stopping XppService'
            [void](Stop-XppService)
            Write-Host ''
        }
        'restart' {
            Write-Host ''
            Write-Head 'Restarting XppService'
            [void](Stop-XppService)
            [void](Start-XppService)
            Write-Host ''
        }
        default   {
            Write-Err "Unknown: dt service $Action"
            Write-Dim 'Expected: status | stop | restart'
            exit 1
        }
    }
}

function Cmd-CacheClear {
    Write-Host ''
    Write-Head 'Clear the index cache'
    Write-Host ''

    $dbPath = Join-Path $env:LOCALAPPDATA 'dynamics-xpp\v2-index.db'
    if (-not (Test-Path $dbPath)) {
        Write-Dim "No cache to clear ($dbPath)."
        Write-Host ''
        return
    }

    $size = '{0:N0} MB' -f ((Get-Item $dbPath).Length / 1MB)
    Write-Host "  cache : $dbPath ($size)"
    Write-Host ''
    Write-Warn 'This forces a full re-index on the next start, which takes a long time'
    Write-Warn 'and re-runs embedding (which costs money if semantic search is on).'
    Write-Dim  'If you are doing this because the service refused to start against a newer'
    Write-Dim  'cache, closing the stale session or running "dt update" is usually cheaper.'
    Write-Host ''

    if (-not (Confirm-Action 'Delete the cache?')) {
        Write-Dim 'Cancelled.'
        Write-Host ''
        return
    }

    # The DB is locked while the service holds it, so stop first.
    [void](Stop-XppService)

    # -wal / -shm siblings must go too, or SQLite reconstructs from them.
    foreach ($suffix in @('', '-wal', '-shm')) {
        $f = "$dbPath$suffix"
        if (Test-Path $f) { Remove-Item -Force $f -ErrorAction SilentlyContinue }
    }

    if (Test-Path $dbPath) {
        Write-Err 'Could not delete the cache -- something still holds it open.'
        Write-Dim 'Close any running Claude Code sessions and try again.'
    } else {
        Write-Ok 'Cache cleared. The next start will re-index from scratch.'
    }
    Write-Host ''
}

function Cmd-Setup {
    Write-Host ''
    Write-Head 'dynamics-xpp setup'
    Write-Host ''

    # dev.ps1 owns the reference-locating logic (finding the metadata
    # assemblies and writing BridgeReferences.props); we don't duplicate it,
    # we just make sure -- via Initialize-PluginDataDir, run before dispatch --
    # that it writes where the build will look.

    Write-Head '[1/3] Locating D365 metadata assemblies'

    # Where dev.ps1 will put the references file -- same rule it uses.
    $propsPath = if ($env:CLAUDE_PLUGIN_DATA) {
        Join-Path $env:CLAUDE_PLUGIN_DATA 'BridgeReferences.props'
    } else {
        Join-Path $PluginRoot 'src\XppMetadataBridge\BridgeReferences.props'
    }
    $propsBefore = $null
    if (Test-Path $propsPath) { $propsBefore = (Get-Item $propsPath).LastWriteTimeUtc }

    & $DevScript -Action setup

    # Check the artifact, NOT $LASTEXITCODE: calling a PowerShell script does
    # not set it, so it would still hold whatever the last native command left
    # behind -- which reads as a failure on a perfectly good setup.
    if (-not (Test-Path $propsPath)) {
        Write-Err 'Setup did not produce BridgeReferences.props. Stopping.'
        Write-Dim  "Expected at: $propsPath"
        exit 1
    }
    if ($propsBefore -and (Get-Item $propsPath).LastWriteTimeUtc -eq $propsBefore) {
        Write-Warn 'BridgeReferences.props was not rewritten; using the existing one.'
    }

    Write-Host ''
    Write-Head '[2/3] Building'
    dotnet build $SolutionFile -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Build failed. Fix the errors above and re-run: dt setup'
        exit 1
    }
    Write-Ok 'Build succeeded.'

    Write-Host ''
    Write-Head '[3/3] Putting dt on your PATH'
    Install-DtShim

    Write-Host ''
    Write-Ok 'Setup complete.'
    Write-Dim 'Try:  dt status'
    Write-Host ''
}

<#
    Write ~/.local/bin/dt.cmd forwarding to this checkout's dt.cmd. A forwarding
    shim rather than a copy, so 'dt update' updates the CLI too -- the thing on
    PATH is a pointer, and the implementation moves with the plugin.
#>
function Install-DtShim {
    $binDir = Join-Path $env:USERPROFILE '.local\bin'
    $target = Join-Path $PSScriptRoot 'dt.cmd'
    $shim   = Join-Path $binDir 'dt.cmd'

    if (-not (Test-Path $target)) {
        Write-Warn "Cannot install the shim: $target is missing."
        return
    }

    if (-not (Test-Path $binDir)) { New-Item -ItemType Directory -Path $binDir -Force | Out-Null }

    if ((Test-Path $shim) -and -not $Yes) {
        $existing = Get-Content $shim -Raw
        if ($existing -notmatch [regex]::Escape($target)) {
            Write-Warn "$shim already exists and points somewhere else."
            if (-not (Confirm-Action 'Overwrite it?' -DefaultYes)) {
                Write-Dim 'Left it alone.'
                return
            }
        }
    }

    @"
@echo off
REM dt - dynamics-xpp CLI (shim). Forwards to the installed plugin, so plugin
REM updates update the CLI. Generated by 'dt setup'; safe to regenerate.
"$target" %*
"@ | Set-Content -Path $shim -Encoding ASCII

    Write-Ok "Installed $shim"

    # PATHEXT resolves .BAT before .CMD, so a leftover dt.bat in the same
    # directory silently wins over the shim we just wrote and 'dt' keeps
    # running the old thing. Worth being loud about -- the symptom (the CLI
    # "not updating") gives no hint of the cause.
    foreach ($shadow in @('dt.bat', 'dt.exe', 'dt.com')) {
        $shadowPath = Join-Path $binDir $shadow
        if (Test-Path $shadowPath) {
            Write-Host ''
            Write-Warn "$shadowPath takes precedence over dt.cmd (PATHEXT order)."
            Write-Warn "Until it is removed or renamed, typing 'dt' will still run it."
            Write-Dim  "If it is the old force-reinstall script, it is superseded by 'dt update'."
            Write-Dim  "  Remove-Item '$shadowPath'"
        }
    }

    $onPath = ($env:PATH -split ';' | Where-Object { $_.TrimEnd('\') -ieq $binDir.TrimEnd('\') })
    if (-not $onPath) {
        Write-Warn "$binDir is not on your PATH."
        Write-Dim  'Add it, or call dt by full path. To add it for your user:'
        Write-Dim  "  setx PATH `"%PATH%;$binDir`""
    }
}

function Cmd-Update {
    Write-Host ''
    Write-Head 'Updating dynamics-xpp'
    Write-Host ''

    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Err "'claude' was not found on PATH -- cannot update the plugin."
        Write-Dim 'Install Claude Code, or update through it directly.'
        exit 1
    }

    $before = Get-PluginVersion

    Write-Head '[1/3] Marketplace'
    claude plugin marketplace update dynamics-tools
    if ($LASTEXITCODE -ne 0) { Write-Warn "marketplace update returned $LASTEXITCODE" }

    Write-Host ''
    Write-Head '[2/3] Plugin'
    claude plugin update xpp@dynamics-tools
    if ($LASTEXITCODE -ne 0) { Write-Warn "plugin update returned $LASTEXITCODE" }

    Write-Host ''
    Write-Head '[3/3] Rebuilding'
    dotnet build $SolutionFile -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Build failed after update.'
        Write-Dim 'Run "dt setup" -- the D365 reference paths may need to be re-located.'
        exit 1
    }
    Write-Ok 'Build succeeded.'

    $after = Get-PluginVersion
    Write-Host ''
    if ($before -and $after -and ($before -ne $after)) {
        Write-Ok "Updated $before -> $after"
    } elseif ($after) {
        Write-Dim "Plugin version: $after (unchanged)"
    }

    # No need to restart anything by hand: a session started after this point
    # notices it is newer than the running service and takes over.
    Write-Dim 'Existing Claude Code sessions keep using the old service until they end.'
    Write-Dim 'The next new session takes over automatically, or force it now:  dt service restart'
    Write-Host ''
}

# =============================================================================
# Dispatch
# =============================================================================

Initialize-PluginDataDir

switch ($Command.ToLowerInvariant()) {
    'setup'   { Cmd-Setup }
    'update'  { Cmd-Update }
    'version' { Cmd-Version }
    'status'  { Cmd-Status }
    'service' {
        # No ternary here: this script must run under Windows PowerShell 5.1,
        # which predates ?: (and ?? and ?.).
        $sub = 'status'
        if ($SubCommand) { $sub = $SubCommand.ToLowerInvariant() }
        Cmd-Service -Action $sub
    }
    'cache'   {
        if ($SubCommand -and $SubCommand.ToLowerInvariant() -eq 'clear') { Cmd-CacheClear }
        else {
            Write-Err "Unknown: dt cache $SubCommand"
            Write-Dim 'Expected: dt cache clear'
            exit 1
        }
    }
    'help'    { Cmd-Help }
    '--help'  { Cmd-Help }
    '-h'      { Cmd-Help }
    default   {
        Write-Host ''
        Write-Err "Unknown command: $Command"
        Cmd-Help
        exit 1
    }
}
