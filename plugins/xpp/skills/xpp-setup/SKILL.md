---
name: xpp-setup
description: Use when the user just installed the dynamics-xpp plugin and needs to get the MCP server working — discover the local Visual Studio 2022 D365 extension, generate the per-machine BridgeReferences.props pointing at the D365 metadata DLLs, and build the Release binaries that .mcp.json launches. Also use when the MCP server fails to start because of a missing props file or missing binaries.
---

# First-time setup for the dynamics-xpp plugin

When a user installs `dynamics-xpp` for the first time, the MCP
server can't launch yet — three things need to happen on the user's
machine first:

1. The plugin needs to know where the local **Visual Studio 2022
   D365 extension** lives (so the bridge can reference the
   `Microsoft.Dynamics.AX.Metadata.*` DLLs from there).
2. A per-machine `BridgeReferences.props` file must be written
   into the plugin's source tree, telling MSBuild that path.
3. The plugin's C# solution must be built (Release) so the binaries
   `.mcp.json` points at actually exist.

This skill walks an agent through doing all three. The agent does the
work directly — no need to invoke an external script (though the
plugin also ships `tools/dev.ps1` for users who want unattended
setup).

---

## Trigger conditions

The agent should reach for this skill when:

- The user says they installed the plugin and ask how to get it
  working.
- The user reports the MCP server "not connecting" / "tool not
  found" / "missing exe" and a `tools/dev.ps1 -Action setup` step
  hasn't happened.
- The user asks to "set up dynamics-xpp" or "configure dynamics-xpp" or
  similar.

If the plugin's binaries already exist at the expected path AND the
props file exists, the skill should report "setup is complete" and
not redo work.

---

## Prerequisites the user must have

These are not things this skill installs — the user has to bring
them. Check up-front and stop with a clear message if missing.

1. **Visual Studio 2022** (any edition: Enterprise / Professional /
   Community / BuildTools). Find it under
   `%ProgramFiles%\Microsoft Visual Studio\2022\<Edition>\`.
2. **The Dynamics 365 Finance and Operations Tools extension** for
   VS 2022. Installed via VS Marketplace; lives under
   `<VS install>\Common7\IDE\Extensions\<random-id>\`. The
   extension's folder contains
   `Microsoft.Dynamics.AX.Metadata.dll` and friends.
3. **.NET 9 SDK** (any minor version ≥ 9.0). Required to build the
   plugin's C# projects. Detect via `dotnet --list-sdks`.
4. **A working D365 dev environment.** The user is presumably on a
   Tier 1 D365 developer VM with `PackagesLocalDirectory`
   present. The plugin won't be useful otherwise.

If any prerequisite is missing, stop and tell the user. Don't
attempt to install them — these are environment decisions the user
should make themselves.

---

## The setup flow

### Step 1 — Detect existing state (idempotency)

Check whether setup has already been done:

- Is `${CLAUDE_PLUGIN_ROOT}/src/XppMetadataBridge/BridgeReferences.props`
  present? If yes, the VS2022 discovery already happened.
- Is `${CLAUDE_PLUGIN_ROOT}/src/XppService.Mcp/bin/Release/net9.0/XppService.Mcp.exe`
  present? If yes, the build already happened.

If both exist, say so and stop. Setup is complete. If only one
exists, do just the missing step.

### Step 2 — Find the Visual Studio 2022 D365 extension

Search the four standard VS 2022 edition paths for the extension
folder that contains `Microsoft.Dynamics.AX.Metadata.dll`:

- `${ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\Extensions\`
- `${ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\Extensions\`
- `${ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\`
- `${ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\Extensions\`

Inside each, the D365 extension is in a folder with a random name
(e.g. `uxkd01ri.02h`). Glob for
`Extensions/*/Microsoft.Dynamics.AX.Metadata.dll` — the parent
directory of any match is the extension path.

If multiple matches exist (multiple VS editions), pick the first
one that's complete; report which one was chosen.

If no match exists:

- VS 2022 isn't installed → tell the user.
- VS 2022 IS installed but the D365 extension isn't → tell the
  user to install "Dynamics 365 Finance and Operations Tools"
  from the VS Marketplace, then re-run setup.

### Step 3 — Verify the required DLLs

The discovered extension folder must contain all four of these:

- `Microsoft.Dynamics.AX.Metadata.dll`
- `Microsoft.Dynamics.AX.Metadata.Core.dll`
- `Microsoft.Dynamics.AX.Metadata.Storage.dll`
- `Microsoft.Dynamics.AX.Core.dll`

If any are missing, report which ones and stop. The user's
extension install is incomplete.

### Step 4 — Write `BridgeReferences.props` to `${CLAUDE_PLUGIN_DATA}`

Write `${CLAUDE_PLUGIN_DATA}/BridgeReferences.props` with this content
(substituting the discovered extension path; XML-escape `&` to `&amp;`):

`${CLAUDE_PLUGIN_DATA}` is Claude Code's per-plugin persistent data
directory (resolves to `~/.claude/plugins/data/{plugin-id}/`). The
props file lives here — not inside the plugin tree — so it survives
plugin updates without needing to be regenerated.

```xml
<Project>
  <!--
    Auto-generated by the dynamics-xpp:xpp-setup skill (or tools/dev.ps1 -Action setup).
    Do not commit. The path below is specific to this dev machine.
  -->
  <PropertyGroup>
    <D365ExtensionPath>{extensionPathHere}</D365ExtensionPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.Dynamics.AX.Metadata">
      <HintPath>$(D365ExtensionPath)\Microsoft.Dynamics.AX.Metadata.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Metadata.Core">
      <HintPath>$(D365ExtensionPath)\Microsoft.Dynamics.AX.Metadata.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Metadata.Storage">
      <HintPath>$(D365ExtensionPath)\Microsoft.Dynamics.AX.Metadata.Storage.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Dynamics.AX.Core">
      <HintPath>$(D365ExtensionPath)\Microsoft.Dynamics.AX.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

The file is per-machine (the discovered VS extension path is
specific to this dev box). Outside Claude Code, the file goes next
to the bridge csproj instead — the csproj detects `CLAUDE_PLUGIN_DATA`
and picks the right location.

### Step 5 — Tell the user to reload plugins

The MCP server is launched via `dotnet run` (the `.mcp.json` command
invokes the SDK directly). That means **no separate build step is
needed during setup** — the first time Claude Code launches the MCP,
`dotnet run` will compile incrementally and produce the binary. Same
for every future plugin update: source change triggers an automatic
rebuild on next invocation, source unchanged means an instant skip.

Tell the user:

> *"Setup is complete. Run `/reload-plugins` (or restart Claude Code)
> so the MCP server picks up the new BridgeReferences.props. The first
> launch will take 15-30 seconds to compile; subsequent launches are
> instant. Then try `xpp_status` to confirm the server is connected."*

### Step 6 — On their reload

When the user invokes `/reload-plugins`, Claude Code re-reads the
plugin's `.mcp.json` and launches `dotnet run` with the
`XppService.Mcp.csproj`. dotnet:

- Restores NuGet packages (one-time, ~20-30s the very first time).
- Builds the solution if any source has changed (or hasn't been
  built yet).
- Launches `XppService.Mcp.exe`, which auto-spawns `XppService.exe`,
  which spawns `XppMetadataBridge.exe`.
- The bridge's csproj imports `BridgeReferences.props` from
  `$env:CLAUDE_PLUGIN_DATA`. If the file isn't there yet, the
  build fails with a clear "not found at <path>" error pointing
  back at this skill.

Once it's up, `dynamics-xpp` shows as connected in `/mcp` and the
`xpp_*` tools become available.

---

## Verifying success after restart

After the user restarts Claude Code, they can check that the MCP
server is alive by:

- Running the `/mcp` command — should list `dynamics-xpp` as connected.
- Asking the agent to call `xpp_status` — should return the index
  state.
- Asking the agent to call `xpp_find_object` with a known table name
  like `CustTable` — should return the object's location.

---

## Re-running setup

The MCP runs through `dotnet run`, so **plugin updates don't require
re-running setup** — `dotnet run` detects the source change and
rebuilds automatically on the next launch. The user might experience
a 10-15 second delay on the first session after a plugin update;
that's the incremental rebuild.

Re-run setup only when:

- Moving to a new dev machine (the BridgeReferences.props is per-
  machine and per-`${CLAUDE_PLUGIN_DATA}` directory, so a new machine
  needs its own).
- The VS 2022 D365 extension was reinstalled / updated to a new
  random-id directory and the existing props file now points at
  a missing path.

When re-running, the skill simply overwrites the existing
`${CLAUDE_PLUGIN_DATA}/BridgeReferences.props` with a fresh one.

---

## Equivalent script

For unattended / CI setup, the plugin ships
`${CLAUDE_PLUGIN_ROOT}/tools/dev.ps1`:

```powershell
.\tools\dev.ps1 -Action setup    # writes BridgeReferences.props
```

The `-Action build` action exists for explicit pre-build (useful for
CI), but under normal Claude Code use the `dotnet run` in
`.mcp.json` handles building automatically when needed.

The setup script detects `$env:CLAUDE_PLUGIN_DATA` and writes the
props there when set. Outside Claude Code, the file goes next to
the bridge csproj (local-dev fallback).

---

## Troubleshooting recipes

### "I get `dotnet` not found"

The user doesn't have .NET 9 SDK installed. Direct them to
https://dotnet.microsoft.com/download for the .NET 9 SDK.

### "The build complains about `Microsoft.Dynamics.AX.Metadata`"

`BridgeReferences.props` is pointing at the wrong path or the path
no longer exists. Delete
`${CLAUDE_PLUGIN_ROOT}/src/XppMetadataBridge/BridgeReferences.props`
and re-run setup.

### "Claude Code says the MCP server failed to start"

Most common causes:

1. The build hasn't happened yet — run setup.
2. The build was Debug but `.mcp.json` expects Release — re-run
   `dotnet build -c Release`.
3. The XppService.exe (the upstream gRPC server the MCP launches)
   is locked or in a bad state. Kill any orphan `XppService.exe` /
   `XppMetadataBridge.exe` processes and let the MCP server
   re-spawn them.

### "The MCP server starts but no tools appear"

The Claude Code MCP discovery may be looking at a stale cache.
Restart Claude Code fully (close all windows). Verify with `/mcp`
that `dynamics-xpp` shows as connected. If it shows as failed, look at
Claude Code's logs for the actual error.
