# OutSystems 11 MCP

This is a fork of [`OutSystems/outsystems11-mcp`](https://github.com/OutSystems/outsystems11-mcp) that adds the **Service Studio MCP Bridge**, for what Service Studio's own MCP server can't do yet: see [Service Studio MCP Bridge](#service-studio-mcp-bridge-this-fork).

**Maintain and evolve your OutSystems 11 estate with any coding agent — governed by the O11 lifecycle.**

OutSystems 11 is the stable, trusted foundation your core operations run on. The OutSystems 11 MCP lets you modernize it with confidence: external coding agents read and evolve your O11 applications through a local MCP server embedded in Service Studio — so you get AI speed without the cost, disruption, or risk of a replatform.

## Why it matters

Years of logic, entities, and integrations shouldn't sit frozen in the platform. The OutSystems 11 MCP opens your application model to the AI tools your teams already use — turning a static estate back into a living, evolvable asset, in place.

- **Evolve in place** — no migration, no replatform. Work on the estate you already run.
- **Governed by the O11 lifecycle** — every change flows through the review and quality gates you already trust.
- **Yours, and only yours** — runs locally in your environment, including on-premises and air-gapped deployments. Nothing leaves your environment.
- **Open and neutral** — works with any MCP-compatible coding agent. Optimized today for Claude Code, with GitHub Copilot (CLI and VS Code), Google Antigravity, Kiro, Cursor and Codex CLI also supported.

## What it is

| Aspect | Details |
| --- | --- |
| **Tool** | A local MCP server, embedded in Service Studio (11.55.91 and later) — the entry point for your agent. |
| **Data** | Your O11 application model, read and written via the OutSystems Model API. |
| **Reach** | Any MCP-compatible agent; optimized today for Claude Code, and packaged for GitHub Copilot (CLI and VS Code), Google Antigravity, Kiro, Cursor and Codex CLI. |
| **Capabilities** | Read and analyze — **Generally Available**. Model writes — **Beta**. |
| **Guardrails** | Every change runs through the O11 lifecycle — nothing leaves your environment. |

## Capability status

| Capability | Status | What it covers |
| --- | --- | --- |
| **Read** | **Generally Available (GA)** | Every read tool — `getDataModel`, `getScreen`, `getServerAction`, `runQuery`, `getValidationMessages`, `listOpenModules`, `searchApps`, … |
| **Write** | **Beta** | Mutating `applyModelApiCode` lambdas (new or changed entities, screens, actions, …), plus `omlMerge`, `omlReset`, `omlRefreshReferences` and `omlPublish`. |

The write capabilities above are Beta Features. OutSystems provides Beta Features to collect customer feedback on non-final capabilities. A Beta Feature can change significantly, including through breaking changes, or OutSystems can discontinue it. For the terms that apply, refer to the [OutSystems Beta Features Agreement](https://www.outsystems.com/legal/beta-features-agreement).

Beta write capabilities are fully usable, but their behavior may still change between Service Studio releases. By default, every write is staged and reviewed through Service Studio's interactive Compare-and-Merge window before anything is accepted into your module.

## Service Studio MCP Bridge (this fork)

This fork adds a second MCP server, the **Service Studio MCP Bridge**, for things Service Studio's own MCP server can't do yet. It runs inside Service Studio and works with the environment Service Studio is signed in to, through Service Studio's own connection, so it also reaches modules that aren't open. It doesn't replace the official server: it only fills its gaps.

| You want to… | Official MCP server | Bridge |
| --- | --- | --- |
| Find modules and applications on the server | Open modules only | `listModules`, `listApplications` |
| See a module's version history: who uploaded what, when, and which version is published | — | `getModuleVersions` |
| Download a module's OML file: the published version, the latest upload or any past version | — | `downloadModule` |
| Find the modules that use a module | — | `getConsumers` |
| Find the modules that use one action, entity, structure or block | — (*Find Usages in all Modules* in Service Studio) | `getElementConsumers` |
| Search the public elements other modules expose | Only the open module's references | `searchPublicElements` |
| See which producers a module can reference | — | `listReferenceable` |
| See an application's dependencies on other applications | — | `getApplicationDependencies` |
| Open a module in Service Studio, with no file to clean up | Newer Service Studio builds only | `openModule` |
| Add a dependency on another module's element to the module you have open | — (upstream issue #10) | `addDependency` |
| Lay out a module's flows and notes in one go | — (by hand, flow by flow) | `layoutFlows` |
| Upload open modules as new versions, without publishing | — (`omlPublish` uploads and publishes) | `uploadModules` |
| Create an application, or change its name, description, colour, icon or kind | — | `createApplication`, `updateApplication` |
| Create a module, or convert one to Service or a Library | — | `createModule`, `convertModule` |

Every result names the environment it came from (host, name and user).

**How it works with the official MCP**

- **The official server comes first.** Agents use a bridge tool only for something the official server can't do.
- **The bridge gives way.** When an update to the official MCP covers a bridge function, the function is removed from the bridge and listed under *Migrated functions* in [`BRIDGE.md`](servicestudio-mcp-oml/BRIDGE.md). Agents then use the official tool, even when a prompt asks for the bridge one.
- **Offered when you need it.** Ask for something the bridge covers while it isn't installed, and your agent offers to install it, even where the official docs call the request unsupported. It installs only after you say yes.

**Requirements:** macOS and the .NET 10 SDK (Windows isn't scripted yet). Tested with Service Studio 11.55.97.

**Install**, from a clone of this fork:

```bash
bridge/scripts/install.sh
```

Then quit and reopen Service Studio, register the bridge with the command the script prints, and start a new agent session.

**Update** by running the same script again from an up-to-date clone, then quitting and reopening Service Studio and starting a new agent session. It keeps your token, port and registration. Agents check the installed bridge's version against [`BRIDGE.md`](servicestudio-mcp-oml/BRIDGE.md) and offer the update when it's out of date.

**Safety:** it never saves or publishes: what it changes (a module it opens, a dependency it adds) stays unsaved in Service Studio for you to keep or undo. It doesn't delete or move modules or applications. It listens on `127.0.0.1` only and requires a token that only you can read. Other .NET apps never load it. Opt out for one launch with `SSBRIDGE_DISABLE=1`, or remove it with `bridge/scripts/uninstall.sh`.

More: [`servicestudio-mcp-oml/BRIDGE.md`](servicestudio-mcp-oml/BRIDGE.md) has the rules agents follow and how this fork is maintained; [`bridge/README.md`](bridge/README.md) covers how the bridge works, its safety, and its tests.

## What you can do

**Understand your estate (GA)** — with zero risk. Just ask your agent:

- **Understand an application** — "Walk me through what this module does and how it's structured."
- **Map dependencies** — "What does this module depend on, and what breaks if I change this action?"
- **Generate documentation** — onboarding guides and process flows for the module open in Service Studio.
- **Surface risk** — flag security, performance, and compliance concerns.
- **Baseline quality** — find dead code and gaps in test coverage.

**Evolve your estate (Beta)** — bounded Model API writes, such as new entities, screens and actions, reviewed through Service Studio's Compare-and-Merge window before anything is accepted.

## What's in this repository

This repo distributes the OutSystems 11 MCP skill — it teaches your agent how to read and evolve the OutSystems 11 module open in Service Studio.

| Layer | Cross-tool standard? | In this repo |
| --- | --- | --- |
| **Skill / knowledge base** | ◑ `SKILL.md` format — shared by Claude Code, Antigravity, Kiro, Codex and Cursor | [`servicestudio-mcp-oml/`](servicestudio-mcp-oml/) — start with `SKILL.md` |
| **Agent instructions** | ✅ `AGENTS.md` — cross-tool standard, read by Copilot, Antigravity, Kiro, Cursor, Codex… and Claude Code | `AGENTS.md`, plus `.github/copilot-instructions.md` for Copilot |
| **MCP client config** | ✗ no standard — every client's file location and schema differ | committed where the client reads from the repo; samples in [`mcp/`](mcp/) where it reads from your home directory |

```
outsystems11-mcp/
├─ servicestudio-mcp-oml/        # the skill: SKILL.md + reference/ + examples/ + docs/
│  ├─ BRIDGE.md                  # this fork: the Service Studio MCP Bridge, its rules and Migrated functions
│  └─ reference/mcp-design-choices.md  # MCP host design choices & caveats worth knowing before you use the Service Studio MCP
├─ bridge/                       # this fork: the Service Studio MCP Bridge's code (.NET), install scripts and tests
├─ AGENTS.md                     # cross-tool agent instructions (the contract, condensed)
├─ CLAUDE.md                     # Claude Code's native instructions file
├─ ARCHITECTURE.md               # system boundary + the tenets governing skill content
├─ CONTRIBUTING.md               # how to change this repo
├─ .github/
│  ├─ copilot-instructions.md    # Copilot always-on instructions
│  ├─ chatmodes/                 # VS Code Copilot chat mode
│  └─ prompts/                   # VS Code Copilot /oml-edit prompt
├─ .mcp.json                     # committed MCP config (Claude Code, in-repo)
├─ .claude/settings.json         # pre-approves the servicestudio MCP server
├─ .vscode/mcp.json              # committed MCP config (Copilot in VS Code)
├─ .kiro/settings/mcp.json       # committed MCP config (Kiro, in-repo)
├─ .cursor/mcp.json              # committed MCP config (Cursor, in-repo)
├─ mcp/                          # MCP config samples for home-directory clients
│  ├─ antigravity.mcp_config.json
│  ├─ copilot-cli.mcp-config.json
│  └─ codex.config.toml
├─ scripts/                      # install.sh / install.ps1 — installs the skill for Claude Code
└─ setup-docs/                   # per-tool setup guides
```

## Getting started (Claude Code scenario)

### 1. Install Service Studio

The MCP server your agent connects to is **embedded in Service Studio** — the skill on its own has nothing to talk to without it. Install or update to **Service Studio 11.55.91 or later**; the MCP server ships in the standard release, with no separate build or activation required.

### 2. Start the MCP server in Service Studio

The MCP server is embedded in Service Studio, but it does **not** start automatically — you start it per session, from the module you want to work on:

1. Open the module in Service Studio.
2. Go to **Edit > MCP Server**.
3. Confirm the **MCP server port** (default `41820`) and click **Start MCP Server**.

The dialog then shows the **MCP server URL**, with a **Copy** button. Use that URL in the command in step 3 below. The default is `http://127.0.0.1:41820/mcp`; adjust the port if you changed it.

### 3. Register the MCP server

Before you open Claude Code and add the skill, register the Service Studio MCP server so your agent knows where to reach it. In a terminal, run this command, using the MCP server URL from step 2:

```
claude mcp add --transport http servicestudio http://127.0.0.1:41820/mcp --scope user
```

This registers an HTTP MCP server named `servicestudio`, across every project and directory, pointing at the local endpoint exposed by Service Studio. Service Studio must be running with a module open **and its MCP server started** (step 2) for the connection to succeed.

> This is what you want for everyday use: the skill installs globally, so you'll be running Claude Code from your own projects. If you are working **inside this repository** instead, you can skip the command — the committed `.mcp.json` already declares the same server and `.claude/settings.json` pre-approves it.

### 4. Add the skill to Claude Code

Clone this repository and run the installer for your platform. It copies `servicestudio-mcp-oml/` into your Claude Code skills directory (`%USERPROFILE%\.claude\skills\` on Windows, or` ~/.claude/skills/` on macOS/Linux), overwriting any existing copy. Re-run the same command any time to update to the latest version.

```
git clone https://github.com/OutSystems/outsystems11-mcp.git
cd outsystems11-mcp
```

**macOS / Linux / Git Bash on Windows**

```
bash scripts/install.sh
```

**Windows PowerShell**

```
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

Start a new Claude Code session afterward so the skill loads.

> Using a different agent? See [Using other coding agents](#using-other-coding-agents) — steps 1, 2 and 5 still apply, only the skill install and MCP registration differ.

### 5. Authorize Service Studio

Open the module you want to work on. The first time your agent issues a command, Service Studio shows an access prompt — approve it to establish the trust handshake between your agent and Service Studio.

If the connection between Claude and Service Studio isn't working, try running `/mcp reconnect servicestudio` in your Claude Code session.

### 6. Verify

Start a new Claude Code session, with a Reactive module open in Service Studio, and ask:

> Summarize the module open in Service Studio.

If you get a summary back, you're ready.

> **Good to know:** your coding agent reaches its model over the network. Everything else — the MCP server and your application model — runs locally in your environment.

### 7. (Optional) Add the Service Studio MCP Bridge

For what the official server can't do yet, such as modules that aren't open, version histories, OML downloads and consumers, install the bridge: see [Service Studio MCP Bridge](#service-studio-mcp-bridge-this-fork). You can also skip this step: your agent offers to install it the first time you ask for something it covers.

## Using other coding agents

Claude Code is the optimized path, and the steps above describe it. The same skill and the same MCP server work with other MCP-compatible agents — each just reads its instructions and its MCP config from a different place:

| Tool | Instructions | MCP config | Setup guide |
| --- | --- | --- | --- |
| **Copilot CLI** (`copilot`) | `AGENTS.md` (auto, from the repo root) | `~/.copilot/mcp-config.json` — sample in [`mcp/`](mcp/) | [setup-docs/copilot-cli.md](setup-docs/copilot-cli.md) |
| **Copilot in VS Code** | `.github/copilot-instructions.md` + chat mode | `.vscode/mcp.json` (committed — nothing to install) | [setup-docs/copilot-vscode.md](setup-docs/copilot-vscode.md) |
| **Antigravity** (`agy`) | skill in `~/.gemini/skills/` or `AGENTS.md` | `~/.gemini/config/mcp_config.json` — sample in [`mcp/`](mcp/) | [setup-docs/antigravity.md](setup-docs/antigravity.md) |
| **Kiro** (`kiro-cli`) | skill in `~/.kiro/skills/` or `AGENTS.md` | `.kiro/settings/mcp.json` (committed — nothing to install) | [setup-docs/kiro.md](setup-docs/kiro.md) |
| **Codex CLI** | skill in `.agents/skills/` or `~/.codex/skills/`, or `AGENTS.md` | `~/.codex/config.toml` — sample in [`mcp/`](mcp/) | [setup-docs/codex.md](setup-docs/codex.md) |
| **Cursor** | skill in `.cursor/skills/` or `.agents/skills/`, or `AGENTS.md` | `.cursor/mcp.json` (committed — nothing to install) | [setup-docs/cursor.md](setup-docs/cursor.md) |

Steps 1, 2 and 5 of Getting started apply to every tool — you still need Service Studio 11.55.91 or later, its MCP server started, and the first-connection approval in Service Studio. Only the skill install and the MCP registration differ. Note that `scripts/install.sh` / `install.ps1` install for **Claude Code only**, so the other tools are a manual copy; each guide above has the exact commands.
