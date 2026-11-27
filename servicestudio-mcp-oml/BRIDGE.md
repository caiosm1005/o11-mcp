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
   conversation. `status` also gives `Capabilities`: whether `openModule`, `addDependency`,
   `layoutFlows`, the application tools (`Applications`), `createModule`, `cloneModule`, `moveModule`, `convertModule`,
   `setReferenceTenantIdentifier` (`ReferenceTenantIdentifier`), `getElementConsumers`'
   usage counts and parts (`UsageCount`) and the dialog watch (`DialogWatch`) work with this Service Studio build, which updates itself. When one
   isn't `ok`, don't call it: tell the user, and give the official way (ask them to open the
   module, to add the dependency in Manage Dependencies, to create or convert it in Service
   Studio, or to run Find Usages in all Modules there).

## What the bridge covers

Service Studio's own MCP server works on the modules open in Service Studio. The bridge reads
the environment Service Studio is signed in to, through Service Studio's own connection, so it
also reaches modules that aren't open. Every result names its environment (host, display name
and user).

| Bridge tool | What it returns | Gap in the official server |
| --- | --- | --- |
| `status` | Bridge and Service Studio versions; each window's module, environment and user; each tab, busy or not, and its dialogs | — (call it first) |
| `listApplications` | The environment's applications and their modules | No list of the server's applications |
| `listModules` | The environment's modules: kind, application, latest version, last upload | `listApps` lists only open modules |
| `getModuleVersions` | A module's version history: number, uploader, date, comment, which one is published | No version history |
| `downloadModule` | A module's OML file: the published version, the latest upload or any version number | No download of an OML file, let alone of a past version |
| `getConsumers` | The modules that reference a module, by name, each with its application | No way to find consumers (upstream issue #3) |
| `getElementConsumers` | The modules that reference one public element (an action, entity, structure, block…), by name, each with its application: the list Service Studio's Find Usages in all Modules starts from. On request, each one's usage count, also of a static record, an attribute or an entity or role action | No Find Usages in all Modules |
| `searchPublicElements` | Public elements across the environment, by name, kind and producer module | Only the open module's references are visible |
| `listReferenceable` | The producers a module can reference, with signature hashes | No equivalent of Manage Dependencies' list |
| `getApplicationDependencies` | An application's dependencies on other applications | No application-level view |
| `openModule` | Opens a module in a new Service Studio tab, as Open from environment does: the published version, or `version` (`latest`, a number); no file is written | Older builds have no `openModule` |
| `addDependency` | Adds a dependency on another module's public element to a module open in Service Studio, unsaved | `AddDependency` fails inside `applyModelApiCode` (upstream issue #10) |
| `layoutFlows` | Lays out a module's logic flows on Service Studio's grid and wraps and places their notes, as one unsaved, undoable step | Layout is the agent's to do by hand, flow by flow (SKILL.md § 3.1) |
| `uploadModules` | Uploads open modules to the environment as new versions, without publishing them, as Service Studio's Upload command does; one at a time, each with its result | Only `omlPublish`, which also compiles and deploys |
| `createApplication` | Creates an empty application on the server: name, kind, description, primary colour, icon | No way to create an application |
| `updateApplication` | Changes an application's name, description, primary colour or icon, or converts it to Service | No way to edit an application |
| `createModule` | Creates a module in an application, as the Create Module dialog does, and opens it unsaved | No way to create a module |
| `cloneModule` | Clones a module, open or published, as Module › Clone does: a new module, named as asked, open and unsaved | No way to clone a module |
| `moveModule` | Moves a module into another application on the server, as the application page's Move does | No way to move a module between applications |
| `convertModule` | Converts an open module to Service or to a Library, as Module › Convert does, unsaved | No way to change a module's kind |
| `setReferenceTenantIdentifier` | Sets Show Tenant Identifier on an open module's reference to a multi-tenant entity, unsaved | The Model API only reads it on a reference |

Long lists come in pages: results say how many there are in all, and take a `limit`.

Requests that need the bridge, because Service Studio's own server can't answer them:

- "Which modules or applications are on the server?", "find module X" when it isn't open:
  `listModules`, `listApplications`.
- "Who changed X, and when?", "which version is published?": `getModuleVersions`.
- "Get or compare an earlier version", "download the OML": `getModuleVersions`, then
  `downloadModule`.
- "Which modules use module X?": `getConsumers`.
- "Which modules use action (entity, structure, block…) X?", "who calls X?", "what breaks if I change X's
  signature?": `getElementConsumers`; "…and how much?": with `countUsages`.
- "Which modules use static record (attribute, entity action) X?", "can I delete record X?":
  `getElementConsumers` with the entity as `element` and X as `part`.
- "Is there a public action, entity or block for Y I can reuse?": `searchPublicElements`.
- "What can this module reference?": `listReferenceable`.
- "What does application A depend on?": `getApplicationDependencies`.
- "Open module X": `openModule`; "…on its latest (unpublished) version": `openModule` with
  `version: "latest"`.
- "Add a dependency on X", or any change that uses another module's element ("create a wrapper
  for action X in module M"): `addDependency`.
- "Lay out the flows", "tidy up this action": `layoutFlows`.
- "Upload all open modules", "save my changes to the server without publishing":
  `uploadModules` (no `modules`: every module open on the environment).
- "Create an application", "a new app for X": `createApplication`; "rename the app", "change its
  icon / colour / description": `updateApplication`.
- "Create a module", "add a service module to app A": `createModule`.
- "Clone module X as Y", "copy X into a new module": `cloneModule`.
- "Move module X to app A", "it's in the wrong application", "it landed in Independent Modules":
  `moveModule`.
- "Make this module a service / a library": `convertModule`; "make the app a Service
  application": `updateApplication` with `kind: Service`, once its modules are Service modules.
- "Show the tenant identifier on the referenced entity X", "read every tenant's records of X":
  `setReferenceTenantIdentifier`.

How the bridge fits with SKILL.md:

- **Naming an open module.** Where a bridge tool acts on a module open in Service Studio
  (`addDependency`, `layoutFlows`, `uploadModules`, `cloneModule`'s source, `moveModule`,
  `convertModule`, `setReferenceTenantIdentifier`), `module` takes what Service Studio's own tools take as
  `eSpaceName`: the `tabId` from `listOpenModules`, the module's `key` (with or without dashes) or
  its name. So pass the same value to both servers. A tabId or key names that tab exactly, and it
  also picks the environment when `host` is left out. A name or key that several open tabs match
  (two environments, two versions of one module) is refused with each candidate's tabId: pass
  the one you mean. A tabId stops matching once its tab closes or holds another module.
- **Module not open (§ 0.1)?** When the server has no `searchApps` or `openModule`, use
  `listModules` (with `filter` or `application`) to confirm the module's exact name, then open it
  with the bridge's `openModule`, only when the user's request names it. Like Open from
  environment, it opens the published version unless you pass `version`: `latest` for the newest
  upload, which may be unpublished work, or a number from `getModuleVersions`. A module already
  open comes back as `alreadyOpen` with the version its tab holds; asking for another version of it
  fails until the user closes that tab. It returns while Service Studio is still opening the module:
  wait until Service Studio's own server lists it. Once the module has loaded, Service Studio checks
  its references against the server ("Getting references from …", which takes a while in a module
  with many producers) and, when some are incompatible or missing, asks whether to manage them now.
  For a few minutes after `openModule`, the bridge cancels that check as its Cancel button does, and
  closes the prompt as its close button does if it comes anyway, so Manage Dependencies doesn't
  open; `status` lists each under `AnsweredPrompts`. Service Studio holds that prompt until the
  module's tab is selected, and opens queued after it wait meanwhile, so the bridge brings each
  module it opens to the front once it has loaded (the tabs switch, as a foreground open does).
  A check can end on its own before the bridge sees it, and its prompt come after another open has
  taken the front: while the tab stays busy with no dialog showing, the bridge brings it forward
  again, less often each time, and watches it for up to an hour. Either way the references stay as the module
  saved them, unchecked: refresh them (§ 3.2) before relying on producers' latest signatures, and
  fix missing ones with `addDependency`.
- **The reference check's other messages.** The check behind that prompt
  (`CheckForIncompatibleOrMissingReferences`) can show two more: the server error its server call
  failed with ("Communication error: There was an error connecting to <host>" has shown up the
  moment a check was cancelled, which most likely cut the call short), or "not logged in". None
  matters to an open through the bridge, and each, like every Service Studio message, is a modal
  dialog for the module's tab that stops it and the opens queued after it. So, for a few minutes
  after `openModule`, the bridge closes any of the check's messages in that module's tab as its
  close button does, which gives the message's own Cancel answer (Service Studio's "not now"), and
  `status` lists each under `AnsweredPrompts` with its type and text. It tells them apart by
  message id, which no translation changes: the missing-dependencies prompt's and every
  server-communication message's (the debugger's included, whose data an open also fetches).
  Other messages, and other tabs, are left for the user.
  `status` → `Windows` says which module's tab each dialog belongs to, and a message's id and type.
- **"Service Studio is busy"** from Service Studio's own server (`omlRefreshReferences`,
  `omlPublish`, `omlMerge`) means the module's tab didn't run anything within a second, and it
  doesn't say why. Call `status` at once, rather than retrying: `Windows` names a dialog in the
  way and the module whose tab it belongs to, and `Tabs` marks each tab `Busy` with the dialogs
  listed for it. A tab busy with no dialog listed is one Service Studio is holding a prompt for
  until it's in front: `openModule` on that module brings its tab forward and closes the
  missing-dependencies prompt if that's what shows (its result says so); then call `status` again.
  Anything else that shows is the user's to answer.
- **A dialog open in Service Studio** stops the module's queue, where `addDependency` and
  `layoutFlows` wait. They fail within seconds, naming the dialog ("Service Studio has a dialog
  open (…)"): ask the user to answer it, then try again. The bridge answers no dialog but the
  reference check's, above. Bridges older than 0.5.1 waited two minutes, past the MCP client's own timeout, so
  the caller saw only "The operation timed out."
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
- **A hidden stub isn't a reference.** An entity that came in only as the type of something the
  module uses (a referenced action's parameter, an attribute) sits under the reference with
  `Hidden` set and only its identifier: expressions that use it fail with `Can't identify`, and
  Manage Dependencies shows it unticked. `addDependency` ticks it (its note says it was a stub)
  and fails if it is still hidden afterwards. Bridges older than 0.5.0 answered
  `AlreadyReferenced` for a stub and changed nothing.
- **The platform's own elements** (`ListAppend`, `Site_Property`, `User`…) come under the producer
  `(System)`, as in Manage Dependencies, and `addDependency` adds them to the module's `(System)`
  reference. Bridges older than 0.3.1 listed them under `ServiceCenter`, and `addDependency`
  answered "doesn't reference … afterwards, so nothing was added" even when it had added them. On
  such a bridge, read the module's references back (`runQuery`
  `Root { References { Name ServerActions { Name } Entities { Name } } }`) before you believe that
  error.
- **Laying flows out (§ 3.1)?** `layoutFlows` lays out every logic flow of the module (or those
  `flows` names) in one call, flows a developer drew included, so use it where the project has
  adopted it. Run it once the edit session has ended (after the last `omlMerge` and
  `endOmlEditSession`): it refuses while a session is open, because that session's next merge
  would put the old positions back. A project can run it from a hook after every merge.
- **Uploading without publishing?** `uploadModules` adds a version on the server for each module,
  as Service Studio's Upload does: nothing is compiled or deployed, and the published version stays
  as it is. Like `omlPublish` (§ 3.2), call it only when the user asks to upload. It uploads the
  module as open in Service Studio, so merge first: an edit session's unmerged changes aren't in
  it, and the result warns when a session is open. Each module's result is `uploaded` with its new
  version number, `unchanged` (no new version: nothing to upload), or `failed` with the reason. When
  the server already has the module's version, Service Studio shows "Upload Skipped": the bridge
  closes it and reports `unchanged`. When Service Studio asks something else during an upload (a
  newer version on the server to merge, say), the result names that dialog: the user answers it in
  Service Studio.
- **Which modules use an element?** `getElementConsumers` answers for one public element, by its
  exact name, the way Service Studio's Find Usages in all Modules starts: it asks the server for the
  modules that reference that element. `getConsumers` answers for the whole module, any element.
  When several producers expose the name, it lists them instead: ask the user which one, then call
  it again with `producer` (and `kind`). `searchPublicElements` finds names by part.
  - **Modules, by default.** Without `countUsages` or `part` it lists modules, not where or how
    often they use the element, and answers in a few seconds.
  - **What the server knows.** That list is the server's, from the consumers' published versions: a
    module open in Service Studio with unpublished changes to its references can be missing from
    it, or listed when it no longer uses the element.
  - **Usage counts.** `countUsages: true` goes on as Find Usages in all Modules does: it loads each
    consumer (as open in Service Studio, unsaved changes included, or else its published version in
    memory, nothing written), finds its reference to the element and counts the places that use it
    (`Usages`: what Find Usages lists, each mention in an expression apart, and a call along with
    what reads its result; a reference entity's own Identifier, Label, Order By and indexes aren't
    counted for an attribute, though Find Usages lists them).
    Modules open in Service Studio that reference the element although the server doesn't list them
    are searched too (`AddedFromOpenTabs`). The consumers that use it come first (`Using` says how
    many), then those with `Usages: 0`: a module that references the element but doesn't use it (an
    unused dependency), or one whose reference is out of date. It takes a few
    seconds per consumer, so use it when the count or the unused references matter, not to list
    consumers. A consumer that couldn't be loaded is under `Unchecked`, with the reason: whether it
    uses the element is unknown.
  - **Long searches.** The search runs in the background, one at a time, and other bridge calls
    go on meanwhile. A call returns the result if the search ends within about 45 s; otherwise it
    returns `Status: Searching` with `Searched`, `Of` and a `Job` id. Call `getElementConsumers`
    again with only `job` until the result comes back: each call waits up to 45 s, so it stays
    under the MCP client's timeout. Asking for the same search again while it runs joins it. A
    finished job stays available for 10 minutes; a search stops after 20 minutes, and what it
    didn't reach is under `Unchecked`.
  - **Parts of an element.** The server tracks the action, entity, structure, block or role, not
    its parts. For an entity's static record, attribute or entity action (`CreateOrUpdate` or
    `CreateOrUpdate<Entity>`; only `Get` when the entity is exposed read-only), a structure's
    attribute, or a role's action (`Check<Role>Role`…), name the entity, structure or role as
    `element` and the part as `part` (`partKind` `Record`, `Attribute` or `Action` when a record and
    an attribute share the name). The bridge searches every consumer of the element, as with
    `countUsages`, and counts the part in each: `Usages: 0` is one that uses the element but not
    the part, listed after those that do. That's the answer before deleting or renaming a record or
    an attribute: `Using: 0`, with nothing under `Unchecked`, means no published consumer or open
    module uses it.
  - **Where it reads the element.** The server takes the element itself, not its name, so the bridge
    reads it from the producer as open in Service Studio, or else loads the producer's published
    version in memory (no file written). For a large producer that isn't open, that takes several
    seconds; the result's `ReadFrom` says which it used. A part not published yet is found only in
    the producer as open.
  - **No extensions.** An extension's elements fail with the reason: Find Usages in all Modules
    loads the element from its module, and an extension has no OML to load, while `getConsumers`
    lists no consumers for an extension either.
- **Creating and editing applications.** `createApplication` and `updateApplication` change the
  server at once, so, like `uploadModules`, call them only when the user asks for that application
  change. `createApplication` makes an empty application (`primaryColor` `#RRGGBB`, `icon` an
  absolute path to a PNG or JPEG) without a template: a Service application gets Service Studio's
  default Service template, the others none. Each returns the application as the server has it.
- **Module kinds follow the application's.** `createModule` makes the kinds Service Studio's
  Create Module dialog offers in that application: its own kind (blank, or from its template with
  `template: true`), a blank Service module in any application, and a ReactiveLibrary in a
  Reactive, Phone or Library application. A Traditional module needs a Traditional application,
  and so on: create one first. Module names are unique in the environment. The application's page
  comes to the front, and the module opens in a new tab, unsaved: it reaches the server once
  published (`omlPublish`, when the user asks), and closing its tab without saving loses it.
- **Cloning a module.** `cloneModule` runs what Service Studio's Module › Clone runs, without its
  file dialog, then gives the clone the name asked for (an undoable rename, as its properties do)
  and the application it publishes into: the one given, or the source's, which has to take the
  module's kind (above). The clone has a new module key and opens in a new tab, unsaved: nothing
  reaches the server until it's published (`omlPublish`, when the user asks), which creates it as
  a new module, with its own, empty tables for its entities. So change what the clone shouldn't
  keep (its entities, say) before its first publish. The source is the module open in Service
  Studio, as its tab has it, unsaved changes included but not what an edit session of Service
  Studio's own server holds unmerged (a warning says so); else its published version, loaded
  without opening it. Service Studio clones from a module's tab, so with no module open in the
  window, open one first. Closing the clone's tab without saving loses it, and a copy saved to a file
  and reopened no longer knows its application. Service Studio's "Module cloned" message is
  closed for you. The clone is registered as pending in its application, as Create Module
  registers a new module, so its first publish creates it there. Bridge 0.11.0 didn't register it,
  and that publish created it in Independent Modules: for a clone made with 0.11.0, check its
  application after that publish (`listModules`) and `moveModule` it where it belongs.
- **Moving a module to another application.** `moveModule` does what the module's Move on Service
  Studio's application page does: a server change, at once, so call it only when the user asks
  for that move (a module in the wrong application, say). The module itself doesn't change and
  nothing is published. It refuses what Service Studio refuses or asks about: a system application
  on either side, Independent Modules as the target, a target that can't take the module's kind
  (an extension or Service module goes anywhere, a Library in a Reactive, Phone or Library
  application, any other module in one of its own kind), and an application's last module, whose
  move would delete the application: that one is the user's to do in Service Studio. It returns
  the application before and after, as the server lists it. A tab holding the module publishes it
  into the new application from then on. A module open but never published isn't on the server:
  for it only the application its first publish creates it in changes (`retargeted`). Bridge
  0.11.0 could fail with `CommunicationException: Login needed` for an open module named with
  `host`, through another window's dropped login: the bridge now uses the tab's own window.
- **Converting a module.** `convertModule` runs Service Studio's own Module › Convert command:
  Traditional, Reactive or Phone to Service, or to ReactiveLibrary, as far as Service Studio offers
  it for that module (a refusal lists what it does offer). It refuses a module with errors, one
  with elements the target kind can't hold (it lists them), and one with an edit session of
  Service Studio's own server: merge and end that first, because Service Studio closes the tab and
  reopens the module converted. The bridge confirms the conversion and answers Service Studio's
  "publish now?" with No (`AnsweredPrompts`); the converted module stays unsaved until published.
  Service Studio reopens the converted module from a file, which reruns its reference check: the
  bridge cancels it as after `openModule`. A Library can't depend on database entities, and even a
  blank module depends on the platform's (`(System)`): remove that dependency (Manage Dependencies)
  before converting to ReactiveLibrary, or the conversion is refused with that reason.
  Not covered: Phone→Reactive, which also changes the application on the server and publishes
  (Module › Convert in Service Studio), and Traditional→Reactive, which is OutSystems' separate
  migration tool, not a Service Studio conversion.
- **Converting an application.** Service Studio converts an application only to Service, and only
  when every module the server has in it is a Service module or an extension. So: convert each
  module (`convertModule`), publish it, then `updateApplication` with `kind: Service`. It refuses
  the Others application, a system one, and one that is Service or a Library already.
- **Show Tenant Identifier on a reference.** A consumer sets it on its own reference to another
  module's multi-tenant entity, so its queries read every tenant's records and see the tenant
  identifier, with the producer untouched. The Model API exposes it read-only on a reference
  (`IEntitySignature.ShowTenantIdentifier`), so `applyModelApiCode` can't set it:
  `setReferenceTenantIdentifier` does, as one unsaved, undoable change. As with `addDependency`, set
  it before `startOmlEditSession` (it refuses while a session or unmerged changes are on the
  module), and read it back with `runQuery` `Root { References { Name Entities { Name
  ShowTenantIdentifier } } }`. It refuses an entity that isn't multi-tenant, where Service Studio
  greys the property out, and a hidden stub (add the dependency first).
- **Comparing with an earlier version**: `getModuleVersions` then `downloadModule` with that
  version number gives you its OML file.

Known limitations: two modules with the same name (an espace and an extension, say) can't be told
apart, and servers on the newer REST module API return no version numbers. `getConsumers` finds
no consumers for an extension (verified 2026-11-22 on one that dozens of modules use), so it can't
tell which modules use one, and `getElementConsumers` refuses an extension's elements. Bridges
before 0.11.1 could fail without `host` with "Service Studio has no application service for this
connection" while a window had no module open; they now use a window that's logged in and has
its services (`Ready` in `status`). If it still happens, pass `host` (from `status`).
See [`bridge/README.md`](../bridge/README.md) for the rest.

## Gaps still open

None right now. Candidates for the bridge that aren't built yet go here, and agents don't offer
the bridge for them. Once one is built and listed under
[What the bridge covers](#what-the-bridge-covers), rule 4 applies to it, and it overrides the
manual step SKILL.md § 8 gives for it.

## Setup

The bridge's code is in the fork's [`bridge/`](../bridge/) folder: a .NET startup hook that runs
inside Service Studio and serves MCP on `http://127.0.0.1:48720/mcp`. It needs macOS (Windows
isn't scripted yet) and the .NET 10 SDK.

**Current version: 0.14.0.** An installed bridge reports its version in `status` (`Bridge`) and
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
4. **Only after an explicit yes:** run `bridge/scripts/install.sh` from the checkout of the fork
   your project already holds (a Git submodule, say), at the commit it's on, so the bridge
   installed is the one this file describes; only without one, clone the fork into a temporary
   folder and run it from there. Then run the registration command it prints (not when updating
   a bridge your agent already lists). If a step fails, show the user its output and stop.
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

The same goes for a tool that `status` lists, even as `ok` under `Capabilities`, but that the
session doesn't offer: the agent keeps the tool list it got when it connected, so tools a bridge
update added after the session started stay missing (seen with Claude Code, 2026-11-25, for
0.11.0's `cloneModule` and `moveModule`). Ask the user to reconnect `servicestudio-bridge` (in
Claude Code, from `/mcp`) or to start a new session.

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
