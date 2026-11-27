# Service Studio MCP Bridge

> Part of this fork of `outsystems11-mcp`. What the bridge is for, and the rule that it gives way
> to Service Studio's own MCP server, are in
> [`servicestudio-mcp-oml/BRIDGE.md`](../servicestudio-mcp-oml/BRIDGE.md). Commands below run from
> the repository root.

MCP access to the OutSystems 11 environment that Service Studio is signed in to, so
a coding agent can list applications and modules, read version histories, consumers and public
elements, download any module version, open modules and add dependencies to an open one, all without a database connection or a person
clicking through Service Studio.

It's a .NET startup hook. Service Studio loads it at start through `DOTNET_STARTUP_HOOKS`, and
it serves MCP on `http://127.0.0.1:48720/mcp`. The hook itself is a small .NET Standard loader
(`ServiceStudioBridge.Hook.dll`) that any .NET app can load. It loads the bridge
(`ServiceStudioBridge.dll`, .NET 10) only inside Service Studio. That's far from Service Studio's own MCP server
(41820 and the ports after it) and below macOS's ephemeral range. Every call goes through
Service Studio's own server connection: its login, its services, the same ones its
*Open from environment* and *Manage Dependencies* dialogs use. The bridge never sees a password
or a session secret.

## Tools

| Tool | What it returns |
| --- | --- |
| `status` | Bridge and Service Studio versions, each window's module, environment (host and name) and user |
| `listApplications` | Applications and their modules |
| `listModules` | Modules: kind, application, current version, last upload |
| `getModuleVersions` | A module's full version history: number, uploader, time, comment, which one is published |
| `getConsumers` | Modules that reference a module, by name, each with its application |
| `getElementConsumers` | Modules that reference one public element (an action, entity, structure…), as *Find Usages in all Modules* lists them before searching each; on request, each one's usage count, also of a static record, attribute or entity action |
| `searchPublicElements` | Public elements, as *Manage Dependencies* searches them |
| `getApplicationDependencies` | An application's dependencies on other applications |
| `listReferenceable` | The producers a module can reference, with signature hashes |
| `downloadModule` | A module's OML file, published, latest or any version number, and its path |
| `openModule` | Opens a module in a new Service Studio tab, as *Open from environment* does (no file written) |
| `closeModule` | Closes a module's tab in Service Studio, as the tab's *Close* does; refuses unsaved changes unless told to discard them |
| `addDependency` | Adds a dependency on another module's public element to a module open in Service Studio (unsaved) |
| `layoutFlows` | Lays out a module's logic flows on Service Studio's grid, notes wrapped and placed (one unsaved, undoable step) |
| `uploadModules` | Uploads open modules to the environment as new versions without publishing them, as Service Studio's *Upload* does |
| `createApplication` | Creates an empty application on the server (name, kind, description, colour, icon) |
| `updateApplication` | Changes an application's name, description, colour or icon, or converts it to Service |
| `createModule` | Creates a module in an application, as *Create Module* does, open and unsaved |
| `cloneModule` | Clones a module, open or published, as *Module › Clone* does, under a new name, open and unsaved |
| `moveModule` | Moves a module into another application on the server, as the application page's *Move* does |
| `convertModule` | Converts an open module to Service or a Library, as *Module › Convert* does, unsaved |
| `setReferenceTenantIdentifier` | Sets *Show Tenant Identifier* on an open module's reference to a multi-tenant entity (unsaved) |

Every result says which environment it came from: host, display name and user.

Pick versions from `getModuleVersions`, then pass the number to `downloadModule`. For example,
"Ana's last upload after the published version" is the highest version above the published one
whose uploader is Ana.

## Which environment

Whichever one Service Studio is signed in to. Nothing in the bridge or its configuration names
an environment: sign in to another one in Service Studio and the next call reads that one. If
Service Studio is signed in to several at once, the tools ask for a `host` instead of guessing.

## Safety

- **Never saves or publishes a module.** `openModule`, `addDependency`, `layoutFlows`,
  `createModule`, `cloneModule`, `convertModule` and `setReferenceTenantIdentifier` change only what's open in
  Service Studio, as unsaved edits;
  you save and publish. `uploadModules` adds versions without publishing them, and
  `createApplication`, `updateApplication` and `moveModule` change applications on the server:
  agents call them only when you ask.
- **No deletes.** Service Studio's services can also delete modules and applications; the bridge
  exposes none of that. `closeModule` refuses a module with unsaved changes; with
  `discardChanges`, which agents pass only when you say to, it drops them (and a module never
  published with them). `moveModule` refuses to move an application's last module, which would
  delete the application.
- **Your access, nothing more.** It acts as the user Service Studio is signed in as, so it can
  do only what that user can.
- **Local and authenticated.** It listens on 127.0.0.1 only. It requires a bearer token kept in
  a file only you can read, and refuses requests that carry an `Origin` header (web pages) or a
  foreign `Host`.
- **Fails soft.** Nothing from Service Studio is bound at compile time: every member is looked
  up by name when a tool runs. A Service Studio update that renames something makes that tool
  answer with an error; Service Studio itself keeps starting normally. `status` says up front
  whether `openModule` and `addDependency` work with the running build (`Capabilities`).
- **Leaves other apps alone.** `DOTNET_STARTUP_HOOKS` reaches every .NET app you open, and a
  hook an app's runtime can't load stops that app from starting. The loader targets .NET
  Standard 2.0, so apps on any runtime load it; anywhere but Service Studio it returns at once,
  and the bridge's assembly is never loaded.
- **Opt out** for one launch with `SSBRIDGE_DISABLE=1`, or for good with `bridge/scripts/uninstall.sh`.

## Known limitations

- **Modules that share a name.** Tools find a module by name. When two modules have the same
  name (an espace and an extension, say), the tool answers with an error naming both and can't
  act on either.

## Install (macOS)

Needs the .NET SDK 10. No package and no admin rights: everything goes in your home folder.

```bash
bridge/scripts/install.sh
```

The script:

- builds the hook and the bridge into `~/Library/Application Support/ServiceStudioBridge/bin/`,
  with a `VERSION` file;
- writes `config.json` (just the port; `--port` changes it), a `token` and `headers.sh` there;
- adds a login item, `~/Library/LaunchAgents/local.servicestudiobridge.plist`, which appends
  the hook to `DOTNET_STARTUP_HOOKS` at every login and keeps any hook already there. macOS
  shows a notice that "Service Studio MCP Bridge" can run in the background, and lists it under
  System Settings › General › Login Items & Extensions. Turning it off there stops the bridge
  from loading after your next login.

Then quit and reopen Service Studio, and register the bridge with Claude Code using the command
the script prints. It uses a `headersHelper`, so the token never sits in a config file.

**Updating the bridge:** run the script again from an up-to-date checkout. It rebuilds the bridge
and keeps the token, the port, the login item and the registration. Then quit and reopen Service
Studio, which keeps the old bridge loaded until then, and start a new agent session. The version
an install has is in `bin/VERSION` and in the `status` tool; the current one is in
[`BRIDGE.md`](../servicestudio-mcp-oml/BRIDGE.md) § Setup, and agents offer the update when
yours is older.

**Service Studio updates** don't affect it. An update replaces the app; the hook, its
configuration and the login item live in your home folder.

**Windows** isn't scripted yet. The same DLLs work: set `DOTNET_STARTUP_HOOKS` to the path of
`ServiceStudioBridge.Hook.dll` once with `setx` (Windows keeps user variables, so no login item is needed). Its files go in
`%LOCALAPPDATA%\ServiceStudioBridge\`.

## Files

| Path (macOS) | Contents |
| --- | --- |
| `~/Library/Application Support/ServiceStudioBridge/config.json` | `port`, read at start |
| `…/bin/VERSION` | The installed bridge's version |
| `…/token`, `…/headers.sh` | The bearer token and the helper that sends it |
| `…/bridge.log` | Start-up and error log |
| `…/downloads/` | Default folder for `downloadModule` |

## Develop

```bash
bridge/tests/smoke.sh
```

```bash
bridge/tests/install-roundtrip.sh
```

```bash
bridge/tests/servicestudio-check.sh
```

`smoke.sh` loads the hook into a fake process named `ServiceStudio` and checks the HTTP and MCP
surface. It also checks that the hook stays out of other processes, and that apps on .NET 8 still
start with it (skipped when no .NET 8 runtime is installed). `install-roundtrip.sh` runs the install and uninstall scripts against a temporary home
folder with a stand-in `launchctl`. Neither one touches Service Studio or your login environment.
The layout `layoutFlows` runs is in `src/Layout/`, apart from Service Studio; read
[`LAYOUT.md`](LAYOUT.md) before changing it.
`servicestudio-check.sh` loads the installed Service Studio's assemblies, without starting it, and
checks that `openModule` and `addDependency` still find every Service Studio member they use: run
it after each Service Studio update.
