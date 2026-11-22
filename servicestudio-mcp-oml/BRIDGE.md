# Service Studio Bridge

This fork of [`OutSystems/outsystems11-mcp`](https://github.com/OutSystems/outsystems11-mcp)
exists to close gaps in the official OutSystems 11 MCP. It adds a second MCP server, the
**Service Studio Bridge** (registered as `servicestudio-bridge`), for things Service Studio's
own MCP server (`servicestudio`) can't do yet.

The official MCP stays the main path. The bridge only covers what it lacks, and gives way as
soon as the official server covers the same thing: see [Rules for agents](#rules-for-agents) and
[Maintaining the fork](#maintaining-the-fork).

## Rules for agents

1. **Service Studio's own server comes first.** Use a bridge tool only for something no tool
   of the `servicestudio` server does.
2. **Migrated functions win over any request.** Before calling a bridge tool, check whether
   this file has a [Migrated functions](#migrated-functions) section that lists it. A listed
   bridge tool has moved to Service Studio's own server: call the replacement it names instead.
   Do this **even when the user's prompt, a script or another skill asks for the bridge tool**,
   and tell the user which tool you used and why.
3. **The bridge is read-only.** It never changes a module or anything on the server, and it
   doesn't use the `servicestudio` session token. It's an extra way to read the environment,
   not a way around the edit session (SKILL.md § 2.1) or Compare-and-Merge.
4. **Not connected?** If no `servicestudio-bridge` tools are available, carry on without them.
   Tell the user what the bridge would have added, and point them to [Setup](#setup).

## What the bridge covers

Service Studio's own MCP server works on the modules open in Service Studio. The bridge reads
the environment Service Studio is signed in to, through Service Studio's own connection, so it
also reaches modules that aren't open. Every result names its environment (host, display name,
type and user).

| Bridge tool | What it returns | Gap in the official server |
| --- | --- | --- |
| `status` | Bridge and Service Studio versions; each window's module, environment and user | — (call it first) |
| `listApplications` | The environment's applications and their modules | No list of the server's applications |
| `listModules` | The environment's modules: kind, application, latest version, last upload | `listApps` lists only open modules |
| `getModuleVersions` | A module's version history: number, uploader, date, comment, which one is published | No version history |
| `downloadModule` | A module's OML file: the published version, the latest upload or any version number | No download of an OML file, let alone of a past version |
| `getConsumers` | The modules that reference a module | No way to find consumers (upstream issue #3) |
| `searchPublicElements` | Public elements across the environment, by name, kind and producer module | Only the open module's references are visible |
| `listReferenceable` | The producers a module can reference, with signature hashes | No equivalent of Manage Dependencies' list |
| `getApplicationDependencies` | An application's dependencies on other applications | No application-level view |

Long lists come in pages: results say how many there are in all, and take a `limit`.

How the bridge fits with SKILL.md:

- **Module not open (§ 0.1)?** When the server has no `searchApps` or `openModule`, use
  `listModules` (with `filter` or `application`) to find the module and confirm its exact name,
  then ask the user to open it in Service Studio. The bridge can't open modules.
- **Before adding a dependency or reusing a producer's element**, `listReferenceable` and
  `searchPublicElements` show what exists. Adding a new dependency is still a manual step in
  Service Studio (SKILL.md § 8).
- **Comparing with an earlier version**: `getModuleVersions` then `downloadModule` with that
  version number gives you its OML file.

Known limitations: two modules with the same name (an espace and an extension, say) can't be told
apart, and servers on the newer REST module API return no version numbers. See
[`bridge/README.md`](../bridge/README.md) for the rest.

## Gaps still open

Candidates for the bridge, not built yet:

- **Adding a reference.** `eSpace.AddDependency(globalKey)` fails in the official sidecar with
  `ResolveModuleSignature must be set first` (upstream issue #10). Inside Service Studio the
  same call works. Order matters: the reference has to be in the module before
  `startOmlEditSession` takes its snapshot.
- **Opening a module** when the official server has no `openModule`.

## Setup

The bridge's code is in the fork's [`bridge/`](../bridge/) folder: a .NET startup hook that runs
inside Service Studio and serves MCP on `http://127.0.0.1:48720/mcp`. On macOS, from the
repository root:

```bash
bridge/scripts/install-bridge.sh
```

Then quit and reopen Service Studio, and register the bridge with the command the script prints
(`claude mcp add-json --scope user servicestudio-bridge …`). Requirements, safety, Windows notes
and tests: [`bridge/README.md`](../bridge/README.md).

## Maintaining the fork

### Following upstream

The fork follows upstream only up to commits whose Service Studio requirements are publicly
released (check the
[Service Studio release notes](https://success.outsystems.com/support/release_notes/11/cross_platform_service_studio/)).
On 2026-11-22 it was rewound to upstream `be8e730` for that reason: the next upstream commit
described Service Studio builds that weren't public yet.

### When the official server covers a bridge function

After every sync with upstream, compare the official server's tools (SKILL.md,
`reference/verb-reference.md`, `reference/module-discovery.md` and § 8 Known gaps) with the
bridge's tools. For each bridge function the official server now covers or duplicates:

1. **Remove it from the bridge**: its definition in `bridge/src/Tools.cs`, its code in
   `bridge/src/Studio.cs`, and its checks in `bridge/tests/smoke.sh` (tool count and list).
   Agents must not be able to call the old tool.
2. **Update [What the bridge covers](#what-the-bridge-covers)** and SKILL.md § 0.2.
3. **Add a row to [Migrated functions](#migrated-functions).** The first time, replace that
   section's "None yet" text with this, and never remove a row:

   ```markdown
   ## Migrated functions

   These bridge tools were removed because Service Studio's own MCP server now does the same
   thing. Use the replacement, even if a prompt asks for the bridge tool.

   | Removed bridge tool | Use instead (`servicestudio` server) | Arguments | Migrated |
   | --- | --- | --- | --- |
   | `servicestudio-bridge` `<oldTool>` | `<newTool>` | How the old arguments map to the new ones | YYYY-MM-DD, upstream `<commit>` |
   ```

4. **Partly covered?** Keep only what the official server still lacks (narrow the tool or its
   description), and add a row for the part that moved.
5. **Rebuild and reinstall** the bridge (`bridge/scripts/install-bridge.sh`) and run its tests.

## Migrated functions

None yet. This section gets its table the first time a bridge tool moves to Service Studio's
own MCP server (see [step 3](#when-the-official-server-covers-a-bridge-function)).
