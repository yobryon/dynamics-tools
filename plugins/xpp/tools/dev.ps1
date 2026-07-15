#!/usr/bin/env pwsh

<#
.SYNOPSIS
    dynamics-xpp plugin build / setup / run helper.

.DESCRIPTION
    Internal automation for the plugin's C# projects under src/. Mirrors
    what the xpp-setup skill does conversationally; useful for unattended
    or CI scenarios.

    Projects:
      - XppMetadataBridge   (src/XppMetadataBridge,   net48)
      - XppService          (src/XppService,           net9.0-windows)
      - XppService.Contracts (src/XppService.Contracts, .proto + codegen)
      - XppService.PingProbe (src/XppService.PingProbe, net9.0 — dev probe)
      - XppService.Mcp      (src/XppService.Mcp,       net9.0 — MCP server)

    Actions:
      setup       Find VS2022 D365 extension, write per-machine
                  BridgeReferences.props so the bridge can resolve the
                  Microsoft.Dynamics.AX.Metadata.* DLLs.
      build       dotnet build dynamics-xpp.sln. Defaults to Release (the
                  configuration .mcp.json expects); pass
                  -Configuration Debug to flip.
      run-service Run XppService in the foreground.
      run-stub    Run XppService.Mcp in the foreground (name is historical).
      test        Run all smoke tests (bridge, service, mcp).
      clean       Remove all build outputs (Debug + Release).
      rebuild-index
                  Disaster-recovery: stop the running XppService, delete
                  the local index database (v2-index.db + WAL/SHM), and
                  exit. The next service launch (triggered by any MCP
                  tool call) will rebuild from scratch as part of normal
                  startup. Takes ~30 min on a full F&O codebase; only
                  run when the cache is corrupt or after a schema-
                  version skew the migration path can't bridge.

.PARAMETER Action
    Which action to perform. Defaults to 'build'.

.PARAMETER Configuration
    'Release' (default) or 'Debug'. Applies to build / run-service /
    run-stub actions.

.EXAMPLE
    .\tools\dev.ps1 -Action setup
    .\tools\dev.ps1 -Action build
    .\tools\dev.ps1 -Action build -Configuration Debug
#>

[CmdletBinding()]
param(
    [ValidateSet('setup', 'build', 'run-service', 'run-stub', 'test', 'clean', 'rebuild-index')]
    [string]$Action = 'build',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Info { param([string]$m) Write-Host $m -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "[OK]    $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "[WARN]  $m" -ForegroundColor Yellow }
function Write-Fail { param([string]$m) Write-Host "[ERROR] $m" -ForegroundColor Red }

switch ($Action) {
    'setup' {
        Write-Info '=== v2 setup: locating D365 metadata assemblies ==='

        $required = @(
            'Microsoft.Dynamics.AX.Metadata.dll',
            'Microsoft.Dynamics.AX.Metadata.Core.dll',
            'Microsoft.Dynamics.AX.Metadata.Storage.dll',
            'Microsoft.Dynamics.AX.Metadata.Patterns.dll',
            'Microsoft.Dynamics.AX.Core.dll'
        )

        # Primary source: the deployed platform's own metadata DLLs under
        # <PackagesLocalDirectory>\bin. This path is STABLE across D365 SDK
        # updates -- unlike the VS2022 extension folder, whose name is
        # randomized and re-created on every platform update, which used to
        # break the bridge build after every update. Read the packages path
        # from the service's appsettings.json so setup and the running service
        # resolve against one source of truth.
        $packages = $null
        $appSettings = Join-Path $repoRoot 'src/XppService/appsettings.json'
        if (Test-Path $appSettings) {
            try {
                $cfg = Get-Content $appSettings -Raw | ConvertFrom-Json
                if ($cfg.D365 -and $cfg.D365.PackagesLocalDirectory) {
                    $packages = $cfg.D365.PackagesLocalDirectory
                }
            } catch {
                Write-Warn "Could not parse appsettings.json ($($_.Exception.Message)); using default packages path."
            }
        }
        if (-not $packages) { $packages = 'J:\AosService\PackagesLocalDirectory' }

        $refPath  = $null
        $refLabel = $null
        $binPath  = Join-Path $packages 'bin'
        $missingInBin = $required | Where-Object { -not (Test-Path (Join-Path $binPath $_)) }
        if ((Test-Path $binPath) -and (-not $missingInBin)) {
            $refPath  = $binPath
            $refLabel = "platform bin (stable across SDK updates): $binPath"
            Write-Ok "Using platform metadata DLLs: $binPath"
        } else {
            # Fallback: scan the VS2022 D365 development extension. Only used
            # when the packages bin is absent or missing a required DLL.
            Write-Warn "Platform bin unusable ($binPath); falling back to VS2022 extension scan."
            $editions  = @('Enterprise', 'Professional', 'Community', 'BuildTools')
            $extRoots  = $editions | ForEach-Object {
                "${env:ProgramFiles}\Microsoft Visual Studio\2022\$_\Common7\IDE\Extensions"
            } | Where-Object { Test-Path $_ }

            $d365Ext = $null
            foreach ($root in $extRoots) {
                $cand = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object {
                    Test-Path (Join-Path $_.FullName 'Microsoft.Dynamics.AX.Metadata.dll')
                } | Select-Object -First 1
                if ($cand) { $d365Ext = $cand; break }
            }

            if (-not $d365Ext) {
                Write-Fail "No metadata source found: neither $binPath nor any VS2022 D365 extension folder."
                Write-Fail "Verify D365:PackagesLocalDirectory in appsettings.json, or install the D365 F&O Tools for VS 2022."
                exit 1
            }

            $extMissing = $required | Where-Object { -not (Test-Path (Join-Path $d365Ext.FullName $_)) }
            if ($extMissing) {
                Write-Fail 'VS2022 D365 extension folder is missing required DLLs:'
                $extMissing | ForEach-Object { Write-Fail "  - $_" }
                exit 1
            }

            $refPath  = $d365Ext.FullName
            $refLabel = "VS2022 extension (fallback): $refPath"
            Write-Ok "Found D365 extension: $refPath"
        }

        # Generate the per-machine references file. We keep this OUT of source
        # control (see .gitignore) because the path is dev-box specific.
        # Under Claude Code, write to $env:CLAUDE_PLUGIN_DATA so the file
        # survives plugin updates. Outside Claude Code (e.g. local dev),
        # fall back to the source tree.
        if ($env:CLAUDE_PLUGIN_DATA) {
            if (-not (Test-Path $env:CLAUDE_PLUGIN_DATA)) {
                New-Item -ItemType Directory -Path $env:CLAUDE_PLUGIN_DATA -Force | Out-Null
            }
            $propsPath = Join-Path $env:CLAUDE_PLUGIN_DATA 'BridgeReferences.props'
            Write-Info "Writing BridgeReferences.props to CLAUDE_PLUGIN_DATA: $env:CLAUDE_PLUGIN_DATA"
        } else {
            $propsPath = Join-Path $repoRoot 'src/XppMetadataBridge/BridgeReferences.props'
            Write-Info "CLAUDE_PLUGIN_DATA not set; writing BridgeReferences.props next to csproj for local dev."
        }
        $refPathForXml = $refPath -replace '&', '&amp;'

        $props = @"
<Project>
  <!--
    Auto-generated by tools/dev.ps1 -Action setup.
    Do not commit. The path below is specific to this dev machine.
    Source: $refLabel
  -->
  <PropertyGroup>
    <D365RefPath>$refPathForXml</D365RefPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.Dynamics.AX.Metadata">
      <HintPath>`$(D365RefPath)\Microsoft.Dynamics.AX.Metadata.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Metadata.Core">
      <HintPath>`$(D365RefPath)\Microsoft.Dynamics.AX.Metadata.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Metadata.Storage">
      <HintPath>`$(D365RefPath)\Microsoft.Dynamics.AX.Metadata.Storage.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Metadata.Patterns">
      <HintPath>`$(D365RefPath)\Microsoft.Dynamics.AX.Metadata.Patterns.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Core">
      <HintPath>`$(D365RefPath)\Microsoft.Dynamics.AX.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
"@
        Set-Content -Path $propsPath -Value $props -Encoding UTF8 -NoNewline
        Write-Ok "Wrote $propsPath"
        Write-Ok 'Setup complete; run "build" next.'
    }
    'build' {
        Write-Info "=== Building solution ($Configuration) ==="
        Push-Location $repoRoot
        try {
            dotnet build dynamics-xpp.sln -c $Configuration
            if ($LASTEXITCODE -ne 0) { Write-Fail 'dotnet build failed.'; exit 1 }
            Write-Ok "Solution built ($Configuration)."
        } finally { Pop-Location }
    }
    'run-service' {
        Write-Info "=== Running XppService ($Configuration) ==="
        Push-Location (Join-Path $repoRoot 'src/XppService')
        try { dotnet run -c $Configuration --no-launch-profile } finally { Pop-Location }
    }
    'run-stub' {
        Write-Info "=== Running XppService.Mcp ($Configuration) ==="
        Push-Location (Join-Path $repoRoot 'src/XppService.Mcp')
        try { dotnet run -c $Configuration --no-launch-profile } finally { Pop-Location }
    }
    'test' {
        Write-Info '=== Running v2 smoke tests ==='
        & (Join-Path $PSScriptRoot 'test-bridge.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Fail 'bridge smoke test failed'; exit 1 }
        & (Join-Path $PSScriptRoot 'test-service.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Fail 'service smoke test failed'; exit 1 }
        & (Join-Path $PSScriptRoot 'test-mcp.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Fail 'mcp smoke test failed'; exit 1 }
        Write-Ok 'all v2 smoke tests passed'
    }
    'rebuild-index' {
        Write-Info '=== Disaster-recovery: nuke the index cache ==='
        Write-Info 'Stopping XppService + bridge processes...'
        Get-Process -Name XppService, XppMetadataBridge -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        $dataDir = Join-Path $env:LOCALAPPDATA 'dynamics-xpp'
        $dbPath  = Join-Path $dataDir 'v2-index.db'
        if (-not (Test-Path $dbPath)) {
            Write-Warn "No index cache found at $dbPath. Nothing to remove."
        } else {
            foreach ($suffix in @('', '-wal', '-shm')) {
                $f = "$dbPath$suffix"
                if (Test-Path $f) {
                    Remove-Item -Force $f -ErrorAction SilentlyContinue
                    Write-Ok "removed $f"
                }
            }
        }
        Write-Info ''
        Write-Info 'Cache cleared. The next MCP tool call will spawn a fresh'
        Write-Info 'XppService which will detect the empty database and kick'
        Write-Info 'a bootstrap walk in the background (~30 min on a full'
        Write-Info 'F&O codebase). Status is visible via xpp_status.'
    }
    'clean' {
        Write-Info '=== Cleaning v2 build outputs ==='
        @(
            'src/XppMetadataBridge/bin', 'src/XppMetadataBridge/obj',
            'src/XppService/bin',        'src/XppService/obj',
            'src/XppService.Contracts/bin', 'src/XppService.Contracts/obj',
            'src/XppService.PingProbe/bin', 'src/XppService.PingProbe/obj',
            'src/XppService.Mcp/bin',    'src/XppService.Mcp/obj'
        ) | ForEach-Object {
            $p = Join-Path $repoRoot $_
            if (Test-Path $p) {
                Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
                Write-Ok "removed $_"
            }
        }
    }
}
