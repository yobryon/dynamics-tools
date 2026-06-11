# dynamics-tools — Claude Code plugin marketplace

A small Claude Code [plugin marketplace](https://docs.claude.com/en/docs/claude-code/plugin-marketplaces)
that distributes the `dynamics-xpp` plugin — a fleet of skills plus
an MCP server for **Microsoft Dynamics 365 Finance & Operations X++
development**.

## What's here

```
dynamics-tools/
├── .claude-plugin/marketplace.json   ← marketplace manifest
└── plugins/
    └── xpp/                          ← the dynamics-xpp plugin
        ├── .claude-plugin/plugin.json
        ├── .mcp.json                  ← wires the MCP server
        ├── skills/                    ← 19 skills (X++ language, AOT types, form patterns)
        ├── src/                       ← C# source for the MCP server
        ├── tools/                     ← setup + dev scripts
        ├── README.md
        └── ROADMAP.md
```

The plugin and its install instructions live in
[`plugins/xpp/README.md`](./plugins/xpp/README.md). This top-level
README only describes the marketplace shape.

## What `dynamics-xpp` does

When installed into a Claude Code session on a D365 dev box, the
plugin teaches Claude how to:

- **Read the AOT** — find objects, search code, inspect method
  bodies, get full XML for any AOT artifact.
- **Author AOT objects** — create / update tables, classes, forms,
  EDTs, enums, label files, and their extensions, via the official
  MS metadata APIs (no template files; round-trips through the same
  serializer VS2022 and the F&O AOT use).
- **Pick the right form UX pattern** — per-pattern skills covering
  the 10 named F&O form patterns plus the 17+ container sub-patterns,
  grounded in current Microsoft Learn guidance.

The MCP server is .NET, runs locally, and uses the official
`Microsoft.Dynamics.AX.Metadata.*` APIs — no network exposure, no
cloud dependency.

## Distribution

Source distribution. The plugin ships C# source; users build it
once during install. The build step is essentially free because
the bridge needs per-machine D365 DLL references anyway (resolved
by `tools/dev.ps1 -Action setup`).

See [`plugins/xpp/README.md`](./plugins/xpp/README.md) for the
user-facing install story.

## License

MIT. See [LICENSE](./LICENSE).
