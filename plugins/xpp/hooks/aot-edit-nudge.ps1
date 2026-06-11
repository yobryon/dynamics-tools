# PreToolUse hook for Edit / Write / MultiEdit on *.xml files under
# the F&O metadata path. Doesn't block — emits a "consider the typed
# tool instead" nudge so the agent gets a reminder before reaching
# for raw on-disk edits.
#
# Behaviour:
#   - Reads the hook payload from stdin (PreToolUse JSON).
#   - If tool == Edit/Write/MultiEdit AND file_path ends in .xml AND
#     looks like a D365 metadata file, emits a JSON response with
#     `permissionDecision: "ask"` plus a `permissionDecisionReason`.
#     The reason surfaces in the permission prompt, nudging the agent
#     to use xpp_patch_*. Exit code stays 0 — no block.
#   - Otherwise: exit 0 with no output (no-op).

$ErrorActionPreference = 'Continue'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    $payload = $raw | ConvertFrom-Json -ErrorAction Stop
    $tool = $payload.tool_name
    if ($tool -ne 'Edit' -and $tool -ne 'Write' -and $tool -ne 'MultiEdit') { exit 0 }

    $path = $payload.tool_input.file_path
    if ([string]::IsNullOrWhiteSpace($path)) { exit 0 }
    if (-not $path.ToLowerInvariant().EndsWith('.xml')) { exit 0 }

    # Heuristic: D365 metadata files live under PackagesLocalDirectory,
    # in a subdirectory matching the AOT element type (AxClass, AxForm,
    # AxTable, AxFormExtension, etc.).
    $normalized = $path -replace '/', '\'
    if ($normalized -notmatch '\\PackagesLocalDirectory\\') { exit 0 }
    if ($normalized -notmatch '\\Ax[A-Z][A-Za-z]+\\') { exit 0 }

    # Extract the AxType segment so the nudge can name the right tool.
    $axType = $null
    if ($normalized -match '\\(Ax[A-Z][A-Za-z]+)\\') { $axType = $Matches[1] }
    $typedTool = if ($axType) {
        # Map common AOT types to their patch tool name. Unmapped types
        # get a generic suggestion.
        switch -Regex ($axType) {
            '^AxTable$'                  { 'xpp_patch_table' ; break }
            '^AxTableExtension$'         { 'xpp_patch_table_extension' ; break }
            '^AxForm$'                   { 'xpp_patch_form' ; break }
            '^AxFormExtension$'          { 'xpp_patch_form_extension' ; break }
            '^AxClass$'                  { 'xpp_patch_class' ; break }
            '^AxEnum$'                   { 'xpp_patch_enum' ; break }
            '^AxEnumExtension$'          { 'xpp_patch_enum_extension' ; break }
            '^AxEdt'                     { 'xpp_patch_edt(_extension)?' ; break }
            '^AxView$'                   { 'xpp_patch_view' ; break }
            '^AxViewExtension$'          { 'xpp_patch_view_extension' ; break }
            '^AxDataEntityView$'         { 'xpp_patch_entity' ; break }
            '^AxDataEntityViewExtension$'{ 'xpp_patch_entity_extension' ; break }
            '^AxMenu$'                   { 'xpp_patch_menu' ; break }
            '^AxMenuExtension$'          { 'xpp_patch_menu_extension' ; break }
            '^AxMenuItem'                { 'xpp_patch_menuitem' ; break }
            '^AxQuery$'                  { 'xpp_patch_query' ; break }
            '^AxService'                 { 'xpp_patch_service(_group)?' ; break }
            '^AxTile$'                   { 'xpp_patch_tile' ; break }
            default                      { "xpp_patch_$(($axType -replace '^Ax','').ToLowerInvariant())" }
        }
    } else { 'the matching xpp_patch_* tool' }

    $reason = "$path looks like a D365 AOT metadata file ($axType). " +
              "Editing on-disk skips the dynamics-xpp typed-tool path, so the " +
              "changeset / .rnrproj / search index won't reflect this change. " +
              "Prefer $typedTool (read with the matching xpp_get_* first, mutate, " +
              "then patch back). Only fall through to raw Edit when the typed " +
              "shape can't express what you need."

    $response = @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'ask'
            permissionDecisionReason = $reason
        }
    }
    $response | ConvertTo-Json -Depth 6 -Compress
    exit 0
}
catch {
    # Don't block tool use on hook bugs — fail open.
    [Console]::Error.WriteLine("aot-edit-nudge hook error: $_")
    exit 0
}
