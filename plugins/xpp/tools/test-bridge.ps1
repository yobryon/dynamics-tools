#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Smoke test for the XppMetadataBridge JSON-RPC stdio loop.

.DESCRIPTION
    Spawns the bridge as a child process with stdin/stdout redirected,
    pipes a handful of JSON-RPC requests at it, reads back the responses,
    and asserts they look right. Closes stdin to signal shutdown and
    waits for the bridge to exit cleanly (exit code 0).

    Covers:
      1. Round-trip ping with echo
      2. Notification (no id => no response) sanity
      3. Unknown method => MethodNotFound error
      4. Malformed JSON => ParseError with id=null
      5. Clean shutdown on stdin close

    Exit codes:
      0  all assertions passed
      1  one or more assertions failed (details on stderr)
      2  could not start bridge / IO failure
#>

[CmdletBinding()]
param(
    [string]$BridgePath,

    # PackagesLocalDirectory + CustomMetadataPath to pass to the bridge.
    # When both are set the listModels case runs; otherwise it's skipped.
    # Defaults to the local Tier 1 VM convention if the path exists.
    [string]$PackagesPath,
    [string]$CustomPath
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot isn't reliably resolved when used as a param default value
# under PS5.1's `-File` invocation, so resolve the default in the body.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
if (-not $BridgePath) {
    $BridgePath = Join-Path $scriptDir '..\src\XppMetadataBridge\bin\Debug\net48\XppMetadataBridge.exe'
}

function Fail([string]$msg) {
    Write-Host "[FAIL] $msg" -ForegroundColor Red
    $script:failed = $true
}
function Pass([string]$msg) {
    Write-Host "[PASS] $msg" -ForegroundColor Green
}

# Auto-detect a usable PackagesLocalDirectory if the caller didn't specify
# one. Convention on this dev VM: J:\AosService\PackagesLocalDirectory. If
# not present we skip the listModels test case rather than failing.
if (-not $PackagesPath) {
    $defaultPackages = 'J:\AosService\PackagesLocalDirectory'
    if (Test-Path $defaultPackages) { $PackagesPath = $defaultPackages }
}
if (-not $CustomPath -and $PackagesPath) { $CustomPath = $PackagesPath }

if (-not (Test-Path $BridgePath)) {
    Write-Host "Bridge not found at $BridgePath - building first." -ForegroundColor Yellow
    dotnet build (Join-Path $scriptDir '..\src\XppMetadataBridge\XppMetadataBridge.csproj') | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $BridgePath)) {
        Write-Host "[ERROR] Could not build or locate the bridge." -ForegroundColor Red
        exit 2
    }
}

$script:failed = $false

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $BridgePath
# PS 5.1's .NET Framework ProcessStartInfo lacks ArgumentList; build a
# single space-separated Arguments string instead. Paths may contain
# spaces, so quote each value.
$argParts = @()
if ($PackagesPath) { $argParts += "--packages=`"$PackagesPath`"" }
if ($CustomPath)   { $argParts += "--custom=`"$CustomPath`"" }
if ($argParts.Count -gt 0) { $psi.Arguments = ($argParts -join ' ') }
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow  = $true
# Note: .NET Framework's ProcessStartInfo lacks Standard*Encoding properties
# (those landed in .NET Core 3+). The bridge sets its own encoding inside,
# and our test payloads are ASCII anyway, so the parent-side default is fine.

$proc = [System.Diagnostics.Process]::Start($psi)
if (-not $proc) {
    Write-Host "[ERROR] Failed to start the bridge process." -ForegroundColor Red
    exit 2
}

# We discard stderr rather than wire up an async reader. The bridge only
# emits a handful of stderr lines and the OS-level pipe buffer is well
# beyond what our short test produces; if we ever stress-test, swap in a
# passthrough reader.

function Send-Request([string]$json) {
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Read-Response {
    $line = $proc.StandardOutput.ReadLine()
    if (-not $line) { return $null }
    return ConvertFrom-Json $line
}

try {
    # --- Test 1: ping round-trip ---------------------------------------------
    Send-Request '{"jsonrpc":"2.0","method":"ping","params":{"echo":"hello"},"id":1}'
    $r = Read-Response
    if (-not $r)                            { Fail 'ping: no response' }
    elseif ($r.id -ne 1)                    { Fail "ping: id mismatch (got $($r.id))" }
    elseif ($r.jsonrpc -ne '2.0')           { Fail "ping: jsonrpc field wrong ($($r.jsonrpc))" }
    elseif (-not $r.result)                 { Fail 'ping: missing result' }
    elseif ($r.result.echo -ne 'hello')     { Fail "ping: echo mismatch ($($r.result.echo))" }
    elseif (-not $r.result.serverTime)      { Fail 'ping: missing serverTime' }
    elseif (-not $r.result.bridgeVersion)   { Fail 'ping: missing bridgeVersion' }
    else { Pass "ping echo=hello, version=$($r.result.bridgeVersion)" }

    # --- Test 2: notification (no id) should produce no response -------------
    Send-Request '{"jsonrpc":"2.0","method":"ping","params":{"echo":"silent"}}'
    # Send a follow-up real request and verify we see ITS response next, not
    # any stray response to the notification.
    Send-Request '{"jsonrpc":"2.0","method":"ping","params":{"echo":"after-notify"},"id":2}'
    $r = Read-Response
    if ($r.id -ne 2 -or $r.result.echo -ne 'after-notify') {
        Fail "notification: expected next response to be id=2/echo=after-notify, got id=$($r.id)/echo=$($r.result.echo)"
    } else {
        Pass 'notification produced no response; next request flowed through'
    }

    # --- Test 3: unknown method ---------------------------------------------
    Send-Request '{"jsonrpc":"2.0","method":"nonexistent","id":3}'
    $r = Read-Response
    if (-not $r.error)                        { Fail 'unknown method: missing error' }
    elseif ($r.error.code -ne -32601)         { Fail "unknown method: wrong code ($($r.error.code))" }
    elseif ($r.id -ne 3)                      { Fail "unknown method: id mismatch ($($r.id))" }
    else { Pass 'unknown method returned -32601 with correct id' }

    # --- Test 4: malformed JSON --------------------------------------------
    Send-Request 'this is not json at all'
    $r = Read-Response
    if (-not $r.error)                        { Fail 'parse error: missing error' }
    elseif ($r.error.code -ne -32700)         { Fail "parse error: wrong code ($($r.error.code))" }
    # Spec says id is null when the request can't be parsed.
    elseif ($null -ne $r.id)                  { Fail "parse error: id should be null (got $($r.id))" }
    else { Pass 'malformed JSON returned -32700 with id=null' }

    # --- Test 5: listModels (gated on D365 install being present) -----------
    if ($PackagesPath) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Send-Request '{"jsonrpc":"2.0","method":"listModels","id":5}'
        $r = Read-Response
        $sw.Stop()
        if (-not $r)                                { Fail 'listModels: no response' }
        elseif ($r.error)                           { Fail "listModels: error $($r.error.code) $($r.error.message)" }
        elseif (-not $r.result.models)              { Fail 'listModels: missing result.models' }
        elseif ($r.result.models.Count -lt 5)       { Fail "listModels: suspiciously few models ($($r.result.models.Count))" }
        else {
            $standard = $r.result.summary.standard
            $custom   = $r.result.summary.custom
            Pass "listModels returned $($r.result.models.Count) models ($standard standard, $custom custom) in $($sw.ElapsedMilliseconds)ms"
        }
    } else {
        Write-Host '[SKIP] listModels: PackagesLocalDirectory not configured' -ForegroundColor DarkGray
    }

    # --- Test 6: listObjects on a known model+type --------------------------
    if ($PackagesPath) {
        # Pick a known-populous model. "Foundation" lives inside the
        # ApplicationSuite package and holds the bulk of standard X++ code
        # (including CustTable). Using a model that we just successfully
        # enumerated via getObjectMethods would be even better; Foundation
        # is reliable on every standard F&O install.
        Send-Request '{"jsonrpc":"2.0","method":"listObjects","params":{"model":"Foundation","axType":"AxClass"},"id":6}'
        $r = Read-Response
        if (-not $r)                            { Fail 'listObjects: no response' }
        elseif ($r.error)                       { Fail "listObjects: error $($r.error.code) $($r.error.message)" }
        elseif ($r.result.count -lt 100)        { Fail "listObjects: Foundation/AxClass returned only $($r.result.count) names (expected many)" }
        else { Pass "listObjects Foundation/AxClass: $($r.result.count) names" }

        # Negative case: bogus axType returns InvalidParams
        Send-Request '{"jsonrpc":"2.0","method":"listObjects","params":{"model":"ApplicationSuite","axType":"AxNonsense"},"id":7}'
        $r = Read-Response
        if ($r.error -and $r.error.code -eq -32602) { Pass 'listObjects rejects unknown axType with -32602' }
        else                                        { Fail "listObjects didn't reject unknown axType (got $($r | ConvertTo-Json -Compress))" }

        # Missing model parameter returns InvalidParams
        Send-Request '{"jsonrpc":"2.0","method":"listObjects","params":{"axType":"AxClass"},"id":8}'
        $r = Read-Response
        if ($r.error -and $r.error.code -eq -32602) { Pass 'listObjects rejects missing model with -32602' }
        else                                        { Fail "listObjects didn't reject missing model (got $($r | ConvertTo-Json -Compress))" }
    }

    # --- Test 7: getObjectMethods on a known object ------------------------
    if ($PackagesPath) {
        Send-Request '{"jsonrpc":"2.0","method":"getObjectMethods","params":{"model":"Foundation","axType":"AxTable","name":"CustTable"},"id":9}'
        $r = Read-Response
        if (-not $r)                          { Fail 'getObjectMethods: no response' }
        elseif ($r.error)                     { Fail "getObjectMethods: error $($r.error.code) $($r.error.message)" }
        elseif ($r.result.count -lt 5)        { Fail "getObjectMethods: CustTable returned only $($r.result.count) methods" }
        else {
            $sample = $r.result.methods[0]
            if (-not $sample.name -or -not $sample.source) {
                Fail "getObjectMethods: sample method missing name or source ($($sample | ConvertTo-Json -Compress))"
            } else {
                Pass "getObjectMethods CustTable: $($r.result.count) methods (sample '$($sample.name)', $($sample.source.Length) chars)"
            }
        }

        # Missing object yields ObjectNotFound (-32001)
        Send-Request '{"jsonrpc":"2.0","method":"getObjectMethods","params":{"model":"Foundation","axType":"AxTable","name":"DefinitelyNotARealTable"},"id":10}'
        $r = Read-Response
        if ($r.error -and $r.error.code -eq -32001) { Pass 'getObjectMethods returns -32001 ObjectNotFound on miss' }
        else                                        { Fail "getObjectMethods didn't return ObjectNotFound (got $($r | ConvertTo-Json -Compress))" }
    }

    # --- Test 8: getStructuralReferences ----------------------------------
    if ($PackagesPath) {
        # A form's datasources are the easiest reliably-populated edge kind.
        # Pick a form we know exists.
        Send-Request '{"jsonrpc":"2.0","method":"getStructuralReferences","params":{"model":"Foundation","axType":"AxForm","name":"CustTable"},"id":11}'
        $r = Read-Response
        if (-not $r)                                  { Fail 'getStructuralReferences: no response' }
        elseif ($r.error)                             { Fail "getStructuralReferences: error $($r.error.code) $($r.error.message)" }
        elseif ($r.result.count -lt 1)                { Fail "getStructuralReferences: CustTable form returned $($r.result.count) edges (expected at least 1 datasource)" }
        else {
            $kinds = ($r.result.references | ForEach-Object { $_.kind } | Sort-Object -Unique) -join ','
            Pass "getStructuralReferences CustTable form: $($r.result.count) edges [$kinds]"
        }
    }

    # --- Test 9: getObjectFull returns methods + references in one call ---
    if ($PackagesPath) {
        Send-Request '{"jsonrpc":"2.0","method":"getObjectFull","params":{"model":"Foundation","axType":"AxTable","name":"CustTable"},"id":12}'
        $r = Read-Response
        if (-not $r)                                  { Fail 'getObjectFull: no response' }
        elseif ($r.error)                             { Fail "getObjectFull: error $($r.error.code) $($r.error.message)" }
        elseif ($r.result.methodCount -lt 5)          { Fail "getObjectFull: CustTable returned only $($r.result.methodCount) methods" }
        else { Pass "getObjectFull CustTable: $($r.result.methodCount) methods + $($r.result.referenceCount) refs in one call" }
    }

    # --- Test 10: listKnownTypes returns the set of indexable AxTypes ------
    if ($PackagesPath) {
        Send-Request '{"jsonrpc":"2.0","method":"listKnownTypes","id":12}'
        $r = Read-Response
        if (-not $r)                          { Fail 'listKnownTypes: no response' }
        elseif ($r.error)                     { Fail "listKnownTypes: error $($r.error.code) $($r.error.message)" }
        elseif ($r.result.count -lt 20)       { Fail "listKnownTypes returned only $($r.result.count) types (expected ~60+)" }
        elseif ('AxClass' -notin $r.result.types) { Fail 'listKnownTypes: AxClass missing from result' }
        elseif ('AxTable' -notin $r.result.types) { Fail 'listKnownTypes: AxTable missing from result' }
        else { Pass "listKnownTypes: $($r.result.count) types (incl AxClass, AxTable)" }
    }

    # --- Test 10: clean shutdown on stdin close ----------------------------
    $proc.StandardInput.Close()
    $exited = $proc.WaitForExit(5000)
    if (-not $exited) {
        Fail 'shutdown: bridge did not exit within 5s of stdin close'
        $proc.Kill()
    } elseif ($proc.ExitCode -ne 0) {
        Fail "shutdown: exit code $($proc.ExitCode) (expected 0)"
    } else {
        Pass 'bridge exited cleanly (code 0) after stdin closed'
    }

} catch {
    Fail "exception during test: $($_.Exception.Message)"
    if (-not $proc.HasExited) { $proc.Kill() }
} finally {
    try {
        if (-not $proc.HasExited) { $proc.Kill() }
    } catch {
        # Process may have exited between the check and the Kill - harmless.
    }
}

if ($script:failed) {
    Write-Host ''
    Write-Host 'Bridge smoke test FAILED.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Bridge smoke test passed.' -ForegroundColor Green
exit 0
