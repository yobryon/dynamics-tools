<#
.SYNOPSIS
    Locate the D365 F&O PackagesLocalDirectory on this machine.

.DESCRIPTION
    Dot-source this and call Find-D365PackagesDirectory.

    The metadata store is NOT always on J:. LCS-deployed Tier 1 VMs put it on
    whichever drive the deployment chose -- K:, C:, D: are all real -- so
    anything that hardcodes a drive letter works on exactly one box. This walks
    the places a D365 dev machine actually records the answer, most
    authoritative first, and validates each candidate before accepting it.

    The ladder:
      1. An explicitly supplied path (config / env var). Always wins.
      2. The AOS web.config's Aos.MetadataDirectory. This is the AOS's own
         answer to "where is my metadata", so it is the most authoritative
         source on the box. Located via IIS's applicationHost.config, or via
         DynamicsDevConfig's WebRoleDeploymentFolder, or a drive scan.
      3. DynamicsDevConfig.xml (%USERPROFILE%\Documents\Visual Studio Dynamics
         365\). The VS dev tooling's own config. Its ApplicationHostConfigFile
         points straight into <packages>\bin, and WebRoleDeploymentFolder gives
         the AosService root whose sibling is the packages dir.
      4. A scan of fixed drives for <drive>:\AosService\PackagesLocalDirectory.

    Validation is the important part: a candidate is only accepted if it looks
    like a real packages directory (it contains bin\Microsoft.Dynamics.AX.
    Metadata.dll -- the assembly the bridge must load). A stale or wrong entry
    in any of these sources therefore falls through to the next rung instead of
    poisoning the result.
#>

# NOTE: deliberately no Set-StrictMode here. This file is dot-sourced, so any
# mode we set would leak into the caller's session and change how THEIR code
# behaves -- which is not ours to decide.

<#
    Does this look like a real PackagesLocalDirectory? We test for the very
    assembly the bridge has to load, so "valid" means "the thing we need will
    actually work", not merely "the folder exists".
#>
function Test-D365PackagesDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
        return Test-Path -LiteralPath (Join-Path $Path 'bin\Microsoft.Dynamics.AX.Metadata.dll') -PathType Leaf
    } catch {
        return $false
    }
}

<#
    Read Aos.MetadataDirectory out of an AOS web.config. This is the AOS
    telling us where its own metadata lives.
#>
function Get-D365PathFromWebConfig {
    param([string]$WebConfigPath)

    if (-not (Test-Path -LiteralPath $WebConfigPath -PathType Leaf)) { return $null }
    try {
        $xml = [xml](Get-Content -LiteralPath $WebConfigPath -Raw)
        $add = $xml.configuration.appSettings.add
        foreach ($key in @('Aos.MetadataDirectory', 'Aos.PackageDirectory', 'Common.BinDir')) {
            $hit = $add | Where-Object { $_.key -eq $key } | Select-Object -First 1
            if ($hit -and $hit.value) { return $hit.value }
        }
    } catch { }
    return $null
}

<#
    Candidate AOS webroot locations, cheapest first. IIS's own
    applicationHost.config is the real answer when IIS hosts the AOS.
#>
function Get-D365WebConfigCandidates {
    param([string]$DevConfigWebRoot)

    $candidates = @()

    # IIS: find the site's physical path. Site name is conventionally
    # AOSService, but take any site whose path looks like an AOS webroot.
    $iisConfig = Join-Path $env:WINDIR 'system32\inetsrv\config\applicationHost.config'
    if (Test-Path -LiteralPath $iisConfig -PathType Leaf) {
        try {
            $xml = [xml](Get-Content -LiteralPath $iisConfig -Raw)
            foreach ($site in $xml.configuration.'system.applicationHost'.sites.site) {
                foreach ($app in @($site.application)) {
                    foreach ($vdir in @($app.virtualDirectory)) {
                        if ($vdir.physicalPath) {
                            $candidates += (Join-Path $vdir.physicalPath 'web.config')
                        }
                    }
                }
            }
        } catch { }
    }

    if ($DevConfigWebRoot) {
        $candidates += (Join-Path $DevConfigWebRoot 'web.config')
    }

    foreach ($drive in Get-D365FixedDrives) {
        $candidates += (Join-Path $drive 'AosService\webroot\web.config')
    }

    return $candidates | Where-Object { $_ } | Select-Object -Unique
}

function Get-D365FixedDrives {
    try {
        return [System.IO.DriveInfo]::GetDrives() |
            Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } |
            ForEach-Object { $_.RootDirectory.FullName }
    } catch {
        return @('C:\')
    }
}

<#
    Parse DynamicsDevConfig.xml, the VS dev tooling's per-user config. Returns
    a hashtable with whatever it could learn: PackagesFromHostConfig (derived
    from ApplicationHostConfigFile, which points into <packages>\bin) and
    WebRoot (WebRoleDeploymentFolder).
#>
function Get-D365DevConfig {
    param([string]$Path)

    if (-not $Path) {
        $Path = Join-Path $env:USERPROFILE 'Documents\Visual Studio Dynamics 365\DynamicsDevConfig.xml'
    }
    $result = @{ PackagesFromHostConfig = $null; WebRoot = $null; Path = $Path }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }

    try {
        $xml = [xml](Get-Content -LiteralPath $Path -Raw)
        $cfg = $xml.DynamicsDevConfig

        # ApplicationHostConfigFile is <packages>\bin\applicationHost.config,
        # so two levels up is the packages directory.
        $hostCfg = $cfg.ApplicationHostConfigFile
        if ($hostCfg) {
            $binDir = Split-Path -Parent $hostCfg      # ...\PackagesLocalDirectory\bin
            if ($binDir) {
                $result.PackagesFromHostConfig = Split-Path -Parent $binDir
            }
        }

        if ($cfg.WebRoleDeploymentFolder) { $result.WebRoot = $cfg.WebRoleDeploymentFolder }
    } catch { }

    return $result
}

<#
.SYNOPSIS
    Return the PackagesLocalDirectory, or $null if it can't be found.

.PARAMETER Explicit
    A configured path. If it validates, it wins outright.

.PARAMETER Trace
    Populated with one line per rung tried, for diagnostics. Pass
    [ref]$myList to see exactly what was checked and why each was rejected.
#>
function Find-D365PackagesDirectory {
    [CmdletBinding()]
    param(
        [string]$Explicit,
        [ref]$Trace
    )

    $log = New-Object System.Collections.Generic.List[string]
    function Note([string]$m) { $log.Add($m) | Out-Null }

    $result = $null

    # --- 1. Explicit configuration -------------------------------------
    if ($Explicit) {
        if (Test-D365PackagesDirectory $Explicit) {
            Note "configured path -> $Explicit [OK]"
            $result = $Explicit
        } else {
            Note "configured path -> $Explicit [rejected: no bin\Microsoft.Dynamics.AX.Metadata.dll]"
        }
    }

    $devConfig = $null
    if (-not $result) { $devConfig = Get-D365DevConfig }

    # --- 2. AOS web.config (the AOS's own answer) ----------------------
    if (-not $result) {
        $webRoot = $null
        if ($devConfig -and $devConfig.WebRoot) { $webRoot = $devConfig.WebRoot }
        foreach ($wc in Get-D365WebConfigCandidates -DevConfigWebRoot $webRoot) {
            $candidate = Get-D365PathFromWebConfig $wc
            if (-not $candidate) { continue }
            if (Test-D365PackagesDirectory $candidate) {
                Note "web.config ($wc) -> $candidate [OK]"
                $result = $candidate
                break
            }
            Note "web.config ($wc) -> $candidate [rejected: not a packages dir]"
        }
        if (-not $result) { Note 'web.config: no usable Aos.MetadataDirectory found' }
    }

    # --- 3. DynamicsDevConfig.xml --------------------------------------
    if (-not $result -and $devConfig) {
        if ($devConfig.PackagesFromHostConfig) {
            if (Test-D365PackagesDirectory $devConfig.PackagesFromHostConfig) {
                Note "DynamicsDevConfig ApplicationHostConfigFile -> $($devConfig.PackagesFromHostConfig) [OK]"
                $result = $devConfig.PackagesFromHostConfig
            } else {
                Note "DynamicsDevConfig ApplicationHostConfigFile -> $($devConfig.PackagesFromHostConfig) [rejected]"
            }
        }
        if (-not $result -and $devConfig.WebRoot) {
            # <root>\AosService\WebRoot -> <root>\AosService\PackagesLocalDirectory
            $sibling = Join-Path (Split-Path -Parent $devConfig.WebRoot) 'PackagesLocalDirectory'
            if (Test-D365PackagesDirectory $sibling) {
                Note "DynamicsDevConfig WebRoleDeploymentFolder sibling -> $sibling [OK]"
                $result = $sibling
            } else {
                Note "DynamicsDevConfig WebRoleDeploymentFolder sibling -> $sibling [rejected]"
            }
        }
        if (-not $result) { Note "DynamicsDevConfig ($($devConfig.Path)): nothing usable" }
    }

    # --- 4. Fixed-drive scan -------------------------------------------
    if (-not $result) {
        foreach ($drive in Get-D365FixedDrives) {
            $candidate = Join-Path $drive 'AosService\PackagesLocalDirectory'
            if (Test-D365PackagesDirectory $candidate) {
                Note "drive scan -> $candidate [OK]"
                $result = $candidate
                break
            }
        }
        if (-not $result) { Note 'drive scan: no <drive>:\AosService\PackagesLocalDirectory found' }
    }

    if ($Trace) { $Trace.Value = $log }
    return $result
}
