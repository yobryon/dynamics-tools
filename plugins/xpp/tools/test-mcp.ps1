#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Smoke test for XppService.Mcp - the MCP-over-stdio frontend.

.DESCRIPTION
    Spawns the MCP server, performs the protocol's three-phase handshake
    (initialize + initialized + tools/list), and asserts the expected tool
    names show up. Does NOT verify tool execution; that requires the
    XppService to be running and is covered by drive-through testing via
    Claude Code itself.

    The handshake follows the JSON-RPC 2.0 framing described in the MCP
    spec: each message is a single line of JSON on stdin/stdout. No
    Content-Length headers (that's the legacy LSP-style framing, which
    MCP stdio does NOT use).

    Exit codes:
      0  handshake succeeded and the expected tools were advertised
      1  one or more assertions failed
      2  could not start the MCP server
#>

[CmdletBinding()]
param(
    [string]$McpExe
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
$repoRoot  = Split-Path -Parent $scriptDir
if (-not $McpExe) {
    $McpExe = Join-Path $repoRoot 'src/XppService.Mcp/bin/Debug/net9.0/XppService.Mcp.exe'
}

function Fail([string]$m) { Write-Host "[FAIL] $m" -ForegroundColor Red; $script:failed = $true }
function Pass([string]$m) { Write-Host "[PASS] $m" -ForegroundColor Green }

$script:failed = $false

if (-not (Test-Path $McpExe)) {
    Write-Host "MCP exe not found; building." -ForegroundColor Yellow
    dotnet build (Join-Path $repoRoot 'src/XppService.Mcp/XppService.Mcp.csproj') | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $McpExe)) {
        Write-Host '[ERROR] Build failed.' -ForegroundColor Red
        exit 2
    }
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $McpExe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)
if (-not $proc) {
    Write-Host '[ERROR] Failed to start XppService.Mcp' -ForegroundColor Red
    exit 2
}

function Send-Msg([string]$json) {
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Read-Until-Id([int]$wantedId, [int]$timeoutMs = 5000) {
    # Blocking ReadLineAsync with a timeout. The MCP server may emit
    # notifications interleaved with our request response; we drain them
    # silently and return only the matching response by id.
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $remaining = [int](($deadline - (Get-Date)).TotalMilliseconds)
        if ($remaining -le 0) { break }
        $task = $proc.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($remaining)) { break }
        $line = $task.Result
        if (-not $line) { continue }
        try { $obj = ConvertFrom-Json $line } catch { continue }
        if ($obj.id -eq $wantedId) { return $obj }
        # Notification or unrelated response — keep draining.
    }
    return $null
}

try {
    # ---- initialize -------------------------------------------------------
    $initId = 1
    $initBody = @{
        jsonrpc = '2.0'
        id      = $initId
        method  = 'initialize'
        params  = @{
            protocolVersion = '2024-11-05'
            capabilities    = @{}
            clientInfo      = @{ name = 'test-mcp.ps1'; version = '0.0.0' }
        }
    } | ConvertTo-Json -Depth 8 -Compress
    Send-Msg $initBody

    $resp = Read-Until-Id $initId 5000

    if (-not $resp)                          { Fail 'initialize: no response within 5s' }
    elseif ($resp.error)                     { Fail "initialize: error $($resp.error.message)" }
    elseif (-not $resp.result.serverInfo)    { Fail 'initialize: missing serverInfo' }
    else { Pass "initialize: server '$($resp.result.serverInfo.name)' v$($resp.result.serverInfo.version)" }

    if (-not $script:failed) {
        # ---- initialized notification ------------------------------------
        Send-Msg '{"jsonrpc":"2.0","method":"notifications/initialized"}'

        # ---- tools/list ---------------------------------------------------
        Send-Msg '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
        $tools = Read-Until-Id 2 5000

        if (-not $tools)              { Fail 'tools/list: no response' }
        elseif ($tools.error)         { Fail "tools/list: error $($tools.error.message)" }
        elseif (-not $tools.result.tools) { Fail 'tools/list: missing tools array' }
        else {
            $names = $tools.result.tools | ForEach-Object { $_.name } | Sort-Object
            Write-Host "  tools advertised: $($names -join ', ')" -ForegroundColor DarkGray

            $expected = @(
                'xpp_find_object', 'xpp_search_pattern', 'xpp_search_code', 'xpp_find_references',
                'xpp_get_object_methods', 'xpp_get_method_source',
                'xpp_status', 'xpp_rebuild_index'
            )
            $missing = $expected | Where-Object { $_ -notin $names }
            if ($missing) {
                Fail "tools/list: missing expected tools: $($missing -join ', ')"
            } else {
                Pass "tools/list: all $($expected.Count) expected xpp_* tools present"
            }
        }
    }
} finally {
    try {
        if (-not $proc.HasExited) {
            $proc.StandardInput.Close()
            $proc.WaitForExit(2000) | Out-Null
            if (-not $proc.HasExited) { $proc.Kill() }
        }
    } catch { }
}

if ($script:failed) {
    Write-Host ''
    Write-Host 'MCP smoke test FAILED.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'MCP smoke test passed.' -ForegroundColor Green
exit 0
