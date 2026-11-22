# Service Studio MCP Bridge

This fork of [`OutSystems/outsystems11-mcp`](https://github.com/OutSystems/outsystems11-mcp)
exists to close gaps in the official OutSystems 11 MCP. It adds a second MCP server, the
**Service Studio MCP Bridge** (registered as `servicestudio-bridge`), for things Service Studio's
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
3. **The bridge is no way around the edit session.** It doesn't use the `servicestudio`
   session token. A change Service Studio's own server can make still goes through the edit
   session (SKILL.md § 2.1) and Compare-and-Merge. What the bridge changes (`openModule`,
   `addDependency`) stays in Service Studio, unsaved: the user saves and publishes.
4. **An official gap is an offer, not a dead end.** When the user asks for something Service
   Studio's own server can't do, look it up in [What the bridge covers](#what-the-bridge-covers)
   before you give the official answer. This applies **even where SKILL.md, its references
   (§ 8 Known gaps included) or other official docs call the request unsupported, out of scope
   or a manual step in Service Studio.** If the bridge covers it, use the bridge. If its tools
   aren't available, offer to install it ([Offering to install](#offering-to-install-agents))
   before you suggest the official workaround. Install it only after the user says yes. If they
   decline, give the official workaround and don't offer again in the same conversation.
5. **Offer only what the bridge does today.** What's under [Gaps still open](#gaps-still-open)
   isn't in the bridge yet: for those requests, give the official guidance and don't offer the
   bridge.
6. **Keep the installed bridge current.** Make `status` your first bridge call in a
   conversation. If its `Bridge` version is older than the [current version](#setup), tools or
   fixes this file describes may be missing: offer the update
   ([Offering to install](#offering-to-install-agents)) before you go on. If the user declines,
   carry on with what the installed bridge offers, and don't offer again in the same
   conversation. `status` also gives `Capabilities`: whether `openModule` and `addDependency`
   work with this Service Studio build, which updates itself. When one isn't `ok`, don't call it:
   tell the user, and give the official way (ask them to open the module, or to add the
   dependency in Manage Dependencies).

## What the bridge covers

Service Studio's own MCP server works on the modules open in Service Studio. The bridge reads
the environment Service Studio is signed in to, through Service Studio's own connection, so it
also reaches modules that aren't open. Every result names its environment (host, display name
and user).

| Bridge tool | What it returns | Gap in the official server |
| --- | --- | --- |
| `status` | Bridge and Service Studio versions; each window's module, environment and user | — (call it first) |
| `listApplications` | The environment's applications and their modules | No list of the server's applications |
| `listModules` | The environment's modules: kind, application, latest version, last upload | `listApps` lists only open modules |
| `getModuleVersions` | A module's version history: number, uploader, date, comment, which one is published | No version history |
| `downloadModule` | A module's OML file: the published version, the latest upload or any version number | No download of an OML file, let alone of a past version |
| `getConsumers` | The modules that reference a module, by name, each with its application | No way to find consumers (upstream issue #3) |
| `searchPublicElements` | Public elements across the environment, by name, kind and producer module | Only the open module's references are visible |
| `listReferenceable` | The producers a module can reference, with signature hashes | No equivalent of Manage Dependencies' list |
| `getApplicationDependencies` | An application's dependencies on other applications | No application-level view |
| `openModule` | Opens a module in a new Service Studio tab, as Open from environment does; no file is written | Older builds have no `openModule` |
| `addDependency` | Adds a dependency on another module's public element to a module open in Service Studio, unsaved | `AddDependency` fails inside `applyModelApiCode` (upstream issue #10) |

Long lists come in pages: results say how many there are in all, and take a `limit`.

Requests that need the bridge, because Service Studio's own server can't answer them:

- "Which modules or applications are on the server?", "find module X" when it isn't open:
  `listModules`, `listApplications`.
- "Who changed X, and when?", "which version is published?": `getModuleVersions`.
- "Get or compare an earlier version", "download the OML": `getModuleVersions`, then
  `downloadModule`.
- "What uses X?", "what breaks if I change X?": `getConsumers`.
- "Is there a public action, entity or block for Y I can reuse?": `searchPublicElements`.
- "What can this module reference?": `listReferenceable`.
- "What does application A depend on?": `getApplicationDependencies`.
- "Open module X": `openModule`.
- "Add a dependency on X", or any change that uses another module's element ("create a wrapper
  for action X in module M"): `addDependency`.

How the bridge fits with SKILL.md:

- **Module not open (§ 0.1)?** When the server has no `searchApps` or `openModule`, use
  `listModules` (with `filter` or `application`) to confirm the module's exact name, then open it
  with the bridge's `openModule`, only when the user's request names it. It returns while Service
  Studio is still opening the module: wait until Service Studio's own server lists it.
- **A change needs another module's element?** Add the dependency first, then
  `startOmlEditSession`: the session's snapshot is the module as open in Service Studio, unsaved
  dependency included, so `applyModelApiCode` can use the element and `omlMerge` keeps it. Not the
  other way round: `omlMerge` is a two-way Compare-and-Merge that keeps the working copy's side,
  so a dependency added to the live module while a session is open would be merged away.
  `addDependency` refuses then. Merge what the session holds (if it's coherent), end it, add the
  dependency, and start a new session. Call `addDependency` with the element's exact name (and
  `kind`, when you know it). When several producers expose that name, it lists them instead: ask
  the user which one, then call it again with `producer`. `searchPublicElements` finds elements
  by part of the name. It reads the module before and after: an element already referenced comes
  back as `AlreadyReferenced` with nothing changed, and one the module can't use (a client action
  in a Service module, for one) fails rather than reporting success.
- **The platform's own elements** (`ListAppend`, `Site_Property`, `User`…) come under the producer
  `(System)`, as in Manage Dependencies, and `addDependency` adds them to the module's `(System)`
  reference. Bridges older than 0.3.1 listed them under `ServiceCenter`, and `addDependency`
  answered "doesn't reference … afterwards, so nothing was added" even when it had added them. On
  such a bridge, read the module's references back (`runQuery`
  `Root { References { Name ServerActions { Name } Entities { Name } } }`) before you believe that
  error.
- **Comparing with an earlier version**: `getModuleVersions` then `downloadModule` with that
  version number gives you its OML file.

Known limitations: two modules with the same name (an espace and an extension, say) can't be told
apart, and servers on the newer REST module API return no version numbers. See
[`bridge/README.md`](../bridge/README.md) for the rest.

## Gaps still open

None right now. Candidates for the bridge that aren't built yet go here, and agents don't offer
the bridge for them. Once one is built and listed under
[What the bridge covers](#what-the-bridge-covers), rule 4 applies to it, and it overrides the
manual step SKILL.md § 8 gives for it.

## Setup

The bridge's code is in the fork's [`bridge/`](../bridge/) folder: a .NET startup hook that runs
inside Service Studio and serves MCP on `http://127.0.0.1:48720/mcp`. It needs macOS (Windows
isn't scripted yet) and the .NET 10 SDK.

**Current version: 0.3.1.** An installed bridge reports its version in `status` (`Bridge`) and
in `~/Library/Application Support/ServiceStudioBridge/bin/VERSION`. An install without that file
is older than 0.2.0.

Installing the skill doesn't install the bridge: the skill installer copies only this folder.
Get the fork, then run the bridge's installer from it:

```bash
git clone --depth 1 https://github.com/caiosm1005/o11-mcp.git
```

```bash
o11-mcp/bridge/scripts/install.sh
```

The installed bridge doesn't need the clone afterwards. To remove it, run
`bridge/scripts/uninstall.sh` from any checkout of the fork. Then:

1. **Quit and reopen Service Studio** from the Dock or Finder (not from a terminal that was open
   before the install), so it loads the bridge.
2. **Register the bridge with your agent**, once. For Claude Code, run the command the installer
   prints (`claude mcp add-json --scope user servicestudio-bridge …`). Other agents: add an HTTP
   MCP server at `http://127.0.0.1:48720/mcp` that sends the header
   `~/Library/Application Support/ServiceStudioBridge/headers.sh` prints.
3. **Start a new agent session.** The bridge's tools appear only in sessions started after it's
   registered.

**Updating** an installed bridge: run `bridge/scripts/install.sh` again from an up-to-date
checkout of the fork. It rebuilds the bridge and keeps its token, port, login item and
registration. Then quit and reopen Service Studio, which keeps the old bridge loaded until then,
and start a new agent session so it sees the new tool list.

Requirements, safety, Windows notes and tests: [`bridge/README.md`](../bridge/README.md).

### Offering to install (agents)

When the user asks for something the bridge covers (rule 4) and no `servicestudio-bridge` tools
are available, even if the official docs call the request unsupported or a manual step, and when
rule 6 finds the installed bridge out of date:

1. **Check whether it's installed already, and at which version.** If
   `~/Library/Application Support/ServiceStudioBridge/bin` exists, its `VERSION` file (or
   `status`, when the tools are available) gives the version. At the
   [current version](#setup), don't reinstall: see
   [Installed, but no tools](#installed-but-no-tools-agents), and register the bridge if your
   agent doesn't list it (step 2 above). Older, or no `VERSION` file: offer an update through steps 2 to 5. In step 3, say that it
   rebuilds the installed bridge and keeps its token, port, login item and registration.
2. **Check that it can be installed:** macOS, and `dotnet --version` 10 or later. If not, tell
   the user what the bridge would add and point them to [`bridge/README.md`](../bridge/README.md),
   without offering.
3. **Say what installing does, then ask.** It builds the bridge into
   `~/Library/Application Support/ServiceStudioBridge`. It adds a login item, "Service Studio
   MCP Bridge", which adds the bridge to `DOTNET_STARTUP_HOOKS` for apps opened from then on; macOS
   shows a notice about it. It registers the bridge with your agent. The tools appear only after
   Service Studio is restarted and a new agent session starts.
4. **Only after an explicit yes:** clone the fork into a temporary folder, run
   `bridge/scripts/install.sh` from it, and run the registration command it prints (not when
   updating a bridge your agent already lists). If a step fails, show the user its output and
   stop.
5. **Hand over:** ask the user to quit and reopen Service Studio and to start a new session.
   Don't restart Service Studio yourself.
6. **Declined?** Carry on without the bridge (or with the installed one, for an update), and
   don't offer again in the same conversation.

### Installed, but no tools (agents)

The bridge runs only while Service Studio does. An agent connects to its MCP servers when a
session starts, so if Service Studio wasn't running then, the session can mark
`servicestudio-bridge` as failed and not try again, even once Service Studio is open (seen with
Claude Code).

1. **Check the port**: `nc -z 127.0.0.1 48720` (the port is in
   `~/Library/Application Support/ServiceStudioBridge/config.json`).
2. **Closed?** Ask the user to open Service Studio from the Dock or Finder and sign in, then
   check again. If it was already running when the bridge was installed or updated, it has to
   be quit and reopened.
3. **Open:** ask the user to start a new session, which connects to the bridge and lists its
   tools. Call the bridge only through those tools, never around them (curl or hand-written
   JSON-RPC).

## Maintaining the fork

### Following upstream

The fork follows upstream only up to commits whose Service Studio requirements are publicly
released (check the
[Service Studio release notes](https://success.outsystems.com/support/release_notes/11/cross_platform_service_studio/)).
On 2026-11-22 it was rewound to upstream `be8e730` for that reason: the next upstream commit
described Service Studio builds that weren't public yet.

### Versioning

Every change to the bridge's code or scripts (`bridge/src/`, `bridge/scripts/`) bumps its
version, in the same commit, in two places: `bridge/src/Directory.Build.props` and **Current
version** in [Setup](#setup). Raise the minor number for a tool added, removed or changed, and
the patch number for a fix. Rule 6 compares an installed bridge with this file, so a change
shipped without a bump never reaches anyone who installed the bridge before it.
`bridge/tests/smoke.sh` fails when the two places differ.

### After a Service Studio update

Service Studio updates itself often, and `openModule` and `addDependency` call into it. They use
only public Service Studio members, whose names its obfuscation leaves alone (it renames internal
and private ones), so an update rarely breaks them. `addDependency` falls back to an internal
routine it finds by its shape when the public route goes. Check after each update:

```bash
bridge/tests/servicestudio-check.sh
```

It loads the installed Service Studio's assemblies without starting it and prints the bridge's
`status` Capabilities. A capability that isn't `ok`, or that works only through its fallback,
needs its lookups in `bridge/src/Studio.cs` updated to the new build (decompile the assembly to
see what changed), then a version bump.

### When the bridge gains a capability

1. Add it to [What the bridge covers](#what-the-bridge-covers): the table and the list of
   requests.
2. Remove it from [Gaps still open](#gaps-still-open).
3. If SKILL.md § 8 Known gaps (or a reference file) calls it unsupported or a manual step, add a
   "This fork:" note there saying the bridge now covers it. Rule 4 then makes agents use the
   bridge, or offer to install it, instead of sending the user to the manual step.
4. **Bump the version** ([Versioning](#versioning)).

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
5. **Bump the version** ([Versioning](#versioning)), rebuild and reinstall the bridge
   (`bridge/scripts/install.sh`), and run its tests.

## Migrated functions

None yet. This section gets its table the first time a bridge tool moves to Service Studio's
own MCP server (see [step 3](#when-the-official-server-covers-a-bridge-function)).
