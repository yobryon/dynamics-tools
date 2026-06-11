# Copilot prompt audit — inbox (CLOSED)

The Copilot prompt audit is **complete**. Every piece of substantive
content extracted from
`Microsoft.Dynamics.Framework.Tools.GitHubCopilot.17.0.dll` has been
processed into either the MCP server's embedded resources or the
`plugins/xpp/` plugin's skill fleet.

## Final disposition tally

- **U** (already used) — 10 XSDs, byte-identical to the embedded
  copies at `src/XppService.Mcp/Resources/Schemas/*.xsd`.
- **M** (merged into existing artifact) — 6 substantive per-type
  prompts (Class, Table, Form, Edt, LabelFile, TableExtension)
  merged with our existing MCP guides into the per-type skills
  under `plugins/xpp/skills/xpp-{class,table,form,edt,labelfile,extension}/`.
- **N** (new artifact) — 4 language-reference files
  (LanguagePromptPrefix/Suffix, PredefinedClasses, PredefinedFunctions)
  became `xpp-language` skill + supporting files. Empty / stub
  enum and extension loader prompts: `xpp-enum` and `xpp-extension`
  written from our knowledge since MS shipped empty content.
- **N** (new artifact) — 10 form-pattern Examples files became
  `xpp-pattern-{name}/` skills with the verbatim XML at
  `examples/example.xml` and a `SKILL.md` explaining the pattern.
  Plus extracted template at
  `xpp-pattern-simple-list/template.xml`.
- **X** (discarded) — 10 pattern Prompt loader 1-liners (each was
  just `"<Pattern> has been loaded."` — replaced by the skill
  description), the `.resources` binary (superseded by the
  extracted .txt files which are now consumed), and the raw
  `copilot_prompts.txt` dump.

## What remains in this directory

- `INDEX.md` (this file) — the audit trail.
- `README.md` — the original extraction notes from
  `misc/d365_extension_notes/`, preserved as durable reference for
  *how* the content was extracted.

The `prompts/` subdirectory has been removed; all 44 files were
processed and removed.

## Where the plugin lives

`plugins/xpp/` at the repo root. See its `README.md` for the skill
inventory. Future MCP / server / etc. components belong alongside as
the plugin matures (the user's eventual repo-reorg plan is to move
the MCP server in under `plugins/xpp/` too so it ships as part of
the plugin bundle).
