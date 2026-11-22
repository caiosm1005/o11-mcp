# Service Studio Bridge

> Part of this fork of `outsystems11-mcp`. What the bridge is for, and the rule that it gives way
> to Service Studio's own MCP server, are in
> [`servicestudio-mcp-oml/BRIDGE.md`](../servicestudio-mcp-oml/BRIDGE.md). Commands below run from
> the repository root.

Read-only MCP access to the OutSystems 11 environment that Service Studio is signed in to, so
a coding agent can list applications and modules, read version histories, consumers and public
elements, and download any module version, all without a database connection or a person
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
| `status` | Bridge and Service Studio versions, each window's module, environment (host, name, type) and user |
| `listApplications` | Applications and their modules |
| `listModules` | Modules: kind, application, current version, last upload |
| `getModuleVersions` | A module's full version history: number, uploader, time, comment, which one is published |
| `getConsumers` | Modules that reference a module |
| `searchPublicElements` | Public elements, as *Manage Dependencies* searches them |
| `getApplicationDependencies` | An application's dependencies on other applications |
| `listReferenceable` | The producers a module can reference, with signature hashes |
| `downloadModule` | A module's OML file, published, latest or any version number, and its path |

Every result says which environment it came from: host, display name, type and user.

Pick versions from `getModuleVersions`, then pass the number to `downloadModule`. For example,
"Ana's last upload after the published version" is the highest version above the published one
whose uploader is Ana.

## Which environment

Whichever one Service Studio is signed in to. Nothing in the bridge or its configuration names
an environment: sign in to another one in Service Studio and the next call reads that one. If
Service Studio is signed in to several at once, the tools ask for a `host` instead of guessing.

## Safety

- **Read-only.** Service Studio's services can also delete and move modules and applications;
  the bridge exposes none of that.
- **Your access, nothing more.** It acts as the user Service Studio is signed in as, so it can
  read only what that user can.
- **Local and authenticated.** It listens on 127.0.0.1 only. It requires a bearer token kept in
  a file only you can read, and refuses requests that carry an `Origin` header (web pages) or a
  foreign `Host`.
- **Fails soft.** Nothing from Service Studio is bound at compile time: every member is looked
  up by name when a tool runs. A Service Studio update that renames something makes that tool
  answer with an error; Service Studio itself keeps starting normally.
- **Leaves other apps alone.** `DOTNET_STARTUP_HOOKS` reaches every .NET app you open, and a
  hook an app's runtime can't load stops that app from starting. The loader targets .NET
  Standard 2.0, so apps on any runtime load it; anywhere but Service Studio it returns at once,
  and the bridge's assembly is never loaded.
- **Opt out** for one launch with `SSBRIDGE_DISABLE=1`, or for good with `bridge/scripts/uninstall-bridge.sh`.

## Known limitations

- **Modules that share a name.** Tools find a module by name. When two modules have the same
  name (an espace and an extension, say), the tool answers with an error naming both and can't
  act on either.

## Install (macOS)

Needs the .NET SDK 10. No package and no admin rights: everything goes in your home folder.

```bash
bridge/scripts/install-bridge.sh
```

The script:

- builds the hook and the bridge into `~/Library/Application Support/ServiceStudioBridge/bin/`;
- writes `config.json` (just the port; `--port` changes it), a `token` and `headers.sh` there;
- adds a login item, `~/Library/LaunchAgents/local.servicestudiobridge.plist`, which appends
  the hook to `DOTNET_STARTUP_HOOKS` at every login and keeps any hook already there. macOS
  shows a notice that "Service Studio Bridge" can run in the background, and lists it under
  System Settings › General › Login Items & Extensions. Turning it off there stops the bridge
  from loading after your next login.

Then quit and reopen Service Studio, and register the bridge with Claude Code using the command
the script prints. It uses a `headersHelper`, so the token never sits in a config file.

**Service Studio updates** don't affect it. An update replaces the app; the hook, its
configuration and the login item live in your home folder.

**Windows** isn't scripted yet. The same DLLs work: set `DOTNET_STARTUP_HOOKS` to the path of
`ServiceStudioBridge.Hook.dll` once with `setx` (Windows keeps user variables, so no login item is needed). Its files go in
`%LOCALAPPDATA%\ServiceStudioBridge\`.

## Files

| Path (macOS) | Contents |
| --- | --- |
| `~/Library/Application Support/ServiceStudioBridge/config.json` | `port`, read at start |
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

`smoke.sh` loads the hook into a fake process named `ServiceStudio` and checks the HTTP and MCP
surface. It also checks that the hook stays out of other processes, and that apps on .NET 8 still
start with it (skipped when no .NET 8 runtime is installed). `install-roundtrip.sh` runs the install and uninstall scripts against a temporary home
folder with a stand-in `launchctl`. Neither one touches Service Studio or your login environment.
