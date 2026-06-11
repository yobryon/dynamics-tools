# xpp-status.ps1
#
# Query the local XppService for index status and print a tidy summary.
# Useful when you want to peek at the indexer's state without burning an
# agent context. Connects over the same named pipe the MCP server uses,
# via the XppService.PingProbe binary's --status mode.
#
# Usage:
#   ./tools/xpp-status.ps1              # default pipe (xpp-service-v2)
#   ./tools/xpp-status.ps1 -PipeName x  # override pipe
#   ./tools/xpp-status.ps1 -Raw         # print the raw JSON instead
#
# Runs the probe via `dotnet run` so there's no separate build step to
# remember — the first call does a fast incremental build, subsequent calls
# skip it. PingProbe depends only on the contracts project, so this never
# touches (or rebuilds) the running XppService.

[CmdletBinding()]
param(
    [string]$PipeName = 'xpp-service-v2',
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path (Split-Path -Parent $here) 'src\XppService.PingProbe\XppService.PingProbe.csproj'
if (-not (Test-Path $csproj)) {
    Write-Error "XppService.PingProbe.csproj not found at $csproj"
    exit 2
}

# Run the probe; capture stdout (the JSON line) into $probeOut. NOTE: the
# variable must NOT be named $raw — PowerShell variable names are
# case-insensitive, so $raw aliases the [switch] $Raw parameter above and
# assigning a string to it throws a "cannot convert String to SwitchParameter".
# stderr (dotnet's build/restore chatter, or probe error text) goes to a temp
# file so it stays available for failure diagnostics without merging into the
# captured stdout.
$errFile = [System.IO.Path]::GetTempFileName()
try {
    $probeOut = & dotnet run --project $csproj -c Release --verbosity quiet -- --status $PipeName 2>$errFile
    $code = $LASTEXITCODE
    $errText = Get-Content -LiteralPath $errFile -Raw -ErrorAction SilentlyContinue
} finally {
    Remove-Item -LiteralPath $errFile -ErrorAction SilentlyContinue
}
if ($code -ne 0) {
    Write-Error "PingProbe (dotnet run) exited $code.`nstdout:`n$probeOut`nstderr:`n$errText"
    exit $code
}
# --verbosity quiet keeps stdout to just the probe's one JSON line, but pull it
# out defensively as the single line that looks like a JSON object.
$json = (@($probeOut) | Where-Object { $_ -match '^\s*\{.*\}\s*$' } | Select-Object -Last 1)
if (-not $json) {
    Write-Error "No JSON status line in probe output.`nstdout:`n$probeOut`nstderr:`n$errText"
    exit 3
}

if ($Raw) {
    Write-Output $json
    return
}

try {
    $s = $json | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Error "Could not parse status JSON. Raw output:`n$json"
    exit 3
}

function Format-Number {
    param([long]$n)
    return $n.ToString('N0')
}

function Format-Timestamp {
    param([string]$iso)
    if ([string]::IsNullOrWhiteSpace($iso)) { return '(never)' }
    try {
        $t = [DateTimeOffset]::Parse($iso).ToLocalTime()
        $age = [DateTimeOffset]::Now - $t
        $ageStr = if ($age.TotalSeconds -lt 60) {
            "$([int]$age.TotalSeconds)s ago"
        } elseif ($age.TotalMinutes -lt 60) {
            "$([int]$age.TotalMinutes)m ago"
        } elseif ($age.TotalHours -lt 48) {
            "$([math]::Round($age.TotalHours, 1))h ago"
        } else {
            "$([int]$age.TotalDays)d ago"
        }
        return "$($t.ToString('yyyy-MM-dd HH:mm:ss')) ($ageStr)"
    } catch {
        return $iso
    }
}

# Color helper - falls back to plain when not interactive.
function W {
    param([string]$label, [string]$value, [ConsoleColor]$valueColor = 'Gray')
    Write-Host ("  {0,-22}" -f $label) -NoNewline
    Write-Host $value -ForegroundColor $valueColor
}

$phaseColor = switch ($s.indexState) {
    'ready'         { 'Green' }
    'sweeping'      { 'Cyan' }
    'warming'       { 'Yellow' }
    'uninitialized' { 'Red' }
    default         { 'Gray' }
}
$bridgeColor = if ($s.bridgeHealthy) { 'Green' } else { 'Red' }

# Semantic-search / embedding subsystem. embeddingState is absent on
# pre-semantic builds of the service; treat that as 'n/a' so an older
# service doesn't make the script look broken.
$embedState = if ($null -ne $s.embeddingState -and $s.embeddingState -ne '') { $s.embeddingState } else { 'n/a' }
$embedColor = switch ($embedState) {
    'ready'       { 'Green' }
    'downloading' { 'Cyan' }
    'absent'      { 'Yellow' }
    'disabled'    { 'DarkGray' }
    'unavailable' { 'Yellow' }
    'error'       { 'Red' }
    default       { 'Gray' }
}

Write-Host ""
Write-Host "  XppService status" -ForegroundColor White
Write-Host "  -----------------" -ForegroundColor DarkGray
W 'bridge:'         $(if ($s.bridgeHealthy) { 'healthy' } else { 'UNHEALTHY' })  $bridgeColor
W 'index state:'    $s.indexState  $phaseColor
W 'index ready:'    $s.indexReady
W 'sweep in flight:' $s.sweepInProgress
Write-Host ""
W 'objects:'        (Format-Number $s.objectCount)
W 'methods:'        (Format-Number $s.methodCount)
W 'references:'     (Format-Number $s.referenceCount)
W 'labels:'         (Format-Number $s.labelCount)
Write-Host ""

# Embedding progress. Show count/total plus a percentage when we have a
# meaningful denominator, so a flooding embedder is visibly making headway.
$embedCount = [long]($s.embeddingCount | ForEach-Object { $_ })
$embedTotal = [long]($s.embeddingTotal | ForEach-Object { $_ })
$embedDetail = if ($embedTotal -gt 0) {
    $pct = [math]::Round(($embedCount / $embedTotal) * 100, 1)
    "$(Format-Number $embedCount) / $(Format-Number $embedTotal)  ($pct%)"
} else {
    Format-Number $embedCount
}
W 'semantic state:'  $embedState  $embedColor
W 'embeddings:'      $embedDetail
Write-Host ""
W 'last sweep:'     (Format-Timestamp $s.lastSweepAt)
W 'last full scan:' (Format-Timestamp $s.lastFullScanAt)
Write-Host ""
