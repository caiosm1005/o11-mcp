using System.Text.Json.Nodes;

namespace ServiceStudioBridge;

internal static class Tools
{
    private sealed record Parameter(string Name, string Type, string Description, bool Required = false);

    /// <summary>ReadOnly false: the tool writes a file or changes what's open in Service Studio.</summary>
    private sealed record Tool(string Name, string Description, Parameter[] Parameters, Func<JsonObject, object?> Run, bool ReadOnly = true);

    private static readonly Parameter Host = new("host", "string", "Environment host name. Only needed when Service Studio is signed in to several environments; by default the bridge uses the one it's signed in to.");

    private static readonly Parameter Module = new("module", "string", "The module's name.", Required: true);

    /// <summary>What a tool acting on an open module says of the module, as Service Studio's own server says of eSpaceName.</summary>
    private const string OpenModuleForms = "its tabId, key or name, as Service Studio's listOpenModules gives them; when several open tabs match a name or key, pass the tabId.";

    private static readonly Parameter Limit = new("limit", "integer", "How many items to return at most (default 200, up to 2000). Results say the total and whether some were left out.");

    private static readonly Parameter Details = new("details", "boolean", "Also return descriptions and keys (longer results).");

    private static readonly Tool[] All =
    [
        new("status",
            "The bridge and Service Studio versions, each Service Studio window (its module, the environment it's signed in to, host and name, and the user), " +
            "Capabilities: whether openModule, closeModule, addDependency, layoutFlows, the application and module tools, setReferenceTenantIdentifier and the dialog watch work with this Service Studio build, " +
            "AnsweredPrompts: the reference checks the bridge cancelled and the check's messages it closed after openModule, each with what it said, " +
            "Tabs: each module tab, whether it's busy (its queue doesn't run anything within a second, which is what Service Studio's own server means by " +
            "\"Service Studio is busy\") and the dialogs listed for it; busy with none listed means Service Studio is holding a prompt for the tab until it's in front, " +
            "and Windows: Service Studio's open windows (title, kind, whether the bridge counts it as a dialog, the module whose tab it belongs to, a message's id and type). " +
            "Call it first, and whenever Service Studio's own server answers that it's busy.",
            [],
            _ => Studio.Status()),
        new("listApplications",
            "Lists the environment's applications and their modules.",
            [Host, new("filter", "string", "Case-insensitive part of the application name."), Limit],
            a => Studio.ListApplications(Text(a, "host"), Text(a, "filter"), Number(a, "limit"))),
        new("listModules",
            "Lists the environment's modules, as Open from environment does: name, kind, application, latest version number and last upload.",
            [Host, new("filter", "string", "Case-insensitive part of the module name."), new("application", "string", "Only this application's modules (exact name)."), Details, Limit],
            a => Studio.ListModules(Text(a, "host"), Text(a, "filter"), Text(a, "application"), Flag(a, "details"), Number(a, "limit"))),
        new("getModuleVersions",
            "A module's full version history, newest first: version number, uploader (user name and display name), upload time, " +
            "comment, and which version is published (none if the module was never published). Use it to pick a version for downloadModule, " +
            "such as someone's last upload after the published one.",
            [Host, Module, Limit],
            a => Studio.GetModuleVersions(Text(a, "host"), Required(a, "module"), Number(a, "limit"))),
        new("getConsumers",
            "The modules that reference (consume) a module, sorted by name, each with its application.",
            [Host, Module, Limit],
            a => Studio.GetConsumers(Text(a, "host"), Required(a, "module"), Number(a, "limit"))),
        new("getElementConsumers",
            "The modules that reference one public element (an action, entity, structure, block…), sorted by name, each with its application: the list " +
            "Service Studio's Find Usages in all Modules starts from, as the server has it from the consumers' published versions. By default it lists " +
            "modules, not where in them the element is used, and is quick. countUsages: true goes on as Find Usages in all Modules does: it loads each " +
            "consumer (as open in Service Studio, or its published version in memory; nothing is written) and adds how many places in it use the element; " +
            "it takes a few seconds per consumer, and a search still running after about 45 s returns Status Searching with a job id instead: call again " +
            "with just job to collect the result. part does the same for a part of the element: an entity's static record, attribute or entity action, a " +
            "structure's attribute, or a role's action. The server knows consumers of top-level elements only, so the element's consumers are searched " +
            "for the part. With counts, the consumers that use it come first, then those with Usages 0 (Using says how many do). The element is found by its exact name across the environment; when several producers expose " +
            "that name, it lists them instead: pass the producer (and kind). An extension's elements aren't covered.",
            [Host,
                new("element", "string", "The public element's exact name: for a static record, an attribute or an entity action, its entity. Required unless job is given."),
                new("producer", "string", "The module that exposes it (exact name), when several do."),
                new("kind", "string", "The element's kind, as searchPublicElements reports it (Action, Entity, Structure, WebBlock…), when one name has several kinds."),
                new("part", "string", "A part of the element to find usages of instead: a static record's identifier, an attribute's name, or an entity or role action " +
                    "(CreateOrUpdate or CreateOrUpdateCustomer, CheckManagerRole). Counts usages in each of the element's consumers."),
                new("partKind", "string", "Record, Attribute or Action, when the part's name is ambiguous (a record and an attribute with one name)."),
                new("countUsages", "boolean", "Search each consumer and count its usages of the element (slower: it loads every consumer). Implied by part."),
                new("job", "string", "The job id a search still running returned (Status Searching): collects its result, or its progress. Pass nothing else."),
                Limit],
            a => Studio.GetElementConsumers(Text(a, "host"), Text(a, "element"), Text(a, "producer"), Text(a, "kind"), Text(a, "part"), Text(a, "partKind"),
                OptionalFlag(a, "countUsages"), Text(a, "job"), Number(a, "limit"))),
        new("searchPublicElements",
            "Searches the public elements that modules in the environment expose, as Manage Dependencies lists them: name, kind and producer module. " +
            "The platform's own elements (ListAppend, Site_Property, User…) come under (System), as in Manage Dependencies.",
            [Host,
                new("text", "string", "Case-insensitive part of the element name."),
                new("kind", "string", "Only this kind, such as Action, ServiceAPIMethod, Entity, StaticEntity, Structure, WebBlock or Theme (a \"Reference\" prefix is optional)."),
                new("producer", "string", "Only elements of this producer module (exact name; (System) for the platform's own elements)."),
                Limit],
            a => Studio.SearchPublicElements(Text(a, "host"), Text(a, "text"), Text(a, "kind"), Text(a, "producer"), Number(a, "limit"))),
        new("getApplicationDependencies",
            "An application's dependencies on other applications.",
            [Host, new("application", "string", "The application's name.", Required: true)],
            a => Studio.GetApplicationDependencies(Text(a, "host"), Required(a, "application"))),
        new("listReferenceable",
            "The producers a module can reference (Manage Dependencies' list): name, kind, runtime kind and signature hash.",
            [Host, Module, new("filter", "string", "Case-insensitive part of the producer name."), Details, Limit],
            a => Studio.ListReferenceable(Text(a, "host"), Required(a, "module"), Text(a, "filter"), Flag(a, "details"), Number(a, "limit"))),
        new("downloadModule",
            "Downloads a module's OML file and returns its path: the published version, the latest upload, or any version number from getModuleVersions.",
            [Host, Module,
                new("version", "string", "\"published\" (default), \"latest\", or a version number from getModuleVersions."),
                new("outputDir", "string", "Absolute folder to save the file in. Defaults to the bridge's downloads folder.")],
            a => Studio.DownloadModule(Text(a, "host"), Required(a, "module"), Text(a, "version"), Text(a, "outputDir")),
            ReadOnly: false),
        new("openModule",
            "Opens a module in Service Studio as Open from environment does: Service Studio downloads it into a new tab itself, so no file is written. " +
            "By default that is the published version (the latest upload when none is published); version opens the latest upload or any other. " +
            "Use it only when Service Studio's own server has no openModule, and only for a module the user's request names. " +
            "A module already open comes back as alreadyOpen with the version its tab holds; asking for another version of it fails until its tab is closed. " +
            "When that tab is busy with no dialog showing (a prompt held until the tab is in front), the bridge brings it forward and closes the missing-dependencies prompt if that's what shows. " +
            "Once it has loaded, the bridge cancels Service Studio's reference check (\"Getting references from …\") as its Cancel button does, and closes " +
            "any message that check shows in the module's tab (the missing-dependencies prompt, so Manage Dependencies doesn't open; the communication error a " +
            "cancelled check can leave; \"not logged in\") as its close button does; status lists each under AnsweredPrompts, and the references stay unchecked. " +
            "It returns once Service Studio starts opening the module: wait until Service Studio's own server lists it before working on it.",
            [Host, Module,
                new("version", "string", "\"published\" (the default), \"latest\" for the newest upload even if unpublished, or a version number from getModuleVersions.")],
            a => Studio.OpenModule(Text(a, "host"), Required(a, "module"), Text(a, "version")),
            ReadOnly: false),
        new("closeModule",
            "Closes a module's tab in Service Studio, as the tab's Close does. Use it only when Service Studio's own server has no closeModule, and only for " +
            "a module you opened (openModule, createModule, cloneModule) or one the user asks to close. It asks nobody: where Service Studio's Close would ask, " +
            "it refuses instead, saying why: an edit session or unmerged changes of Service Studio's own server on the module, a dialog or held prompt in its " +
            "tab, a server operation (a publish) running on it, or unsaved changes (merged ones included; a module never published is all unsaved). " +
            "discardChanges closes it anyway and drops those changes for good: pass it only when the user says to. A window's last module tab closes " +
            "like any other; the window stays, on its start page. Returns the tab closed and whether changes were discarded.",
            [Host,
                new("module", "string", "The module open in Service Studio: " + OpenModuleForms, Required: true),
                new("discardChanges", "boolean", "Close even with unsaved changes, which are lost (a module never published is lost whole). Only when the user says to drop them.")],
            a => Studio.CloseModule(Text(a, "host"), Required(a, "module"), Flag(a, "discardChanges")),
            ReadOnly: false),
        new("addDependency",
            "Adds a dependency on another module's public element (an action, entity, structure, block…) to a module open in Service Studio, " +
            "as Search in other modules › Add dependency does. It finds the element by its exact name across the environment; when several " +
            "producers expose that name, it lists them instead: ask the user which one, then pass its producer. The open module gets an unsaved " +
            "change; nothing is saved or published. Add dependencies before startOmlEditSession: it refuses while Service Studio's own server " +
            "has an edit session or unmerged changes on the module, because that session's next omlMerge would remove the dependency. " +
            "It checks the module before and after: an element already referenced comes back as AlreadyReferenced (a hidden stub, an entity that " +
            "came in only as a parameter's or attribute's type, isn't a reference: it gets ticked), and one the module can't use " +
            "(a client action in a Service module, for one) fails instead of reporting success.",
            [Host,
                new("module", "string", "The module open in Service Studio that gets the dependency: " + OpenModuleForms, Required: true),
                new("element", "string", "The public element's exact name.", Required: true),
                new("producer", "string", "The producer module that exposes it (exact name; (System) for the platform's own elements), when several do."),
                new("kind", "string", "The element's kind, as searchPublicElements reports it (Action, Entity, Structure, WebBlock…), when one name has several kinds.")],
            a => Studio.AddDependency(Text(a, "host"), Required(a, "module"), Required(a, "element"), Text(a, "producer"), Text(a, "kind")),
            ReadOnly: false),
        new("uploadModules",
            "Uploads open modules to the environment without publishing them, as Service Studio's Upload command does: each gets a new version on the server " +
            "(getModuleVersions lists it), nothing is compiled or deployed, and the published version stays as it is. By default every module open in Service Studio " +
            "on the environment, one at a time; each result says uploaded (with the new version number), unchanged (no new version), or failed (with the reason). " +
            "It uploads the module as open in Service Studio: changes an edit session of Service Studio's own server hasn't merged aren't in it (the result warns). " +
            "When the server already has a module's version, Service Studio says Upload Skipped: the bridge closes that message and reports the module unchanged. " +
            "Service Studio may ask something else while uploading (a newer version on the server to merge, say): the result names that dialog for the user to answer.",
            [Host, new("modules", "string", "Modules to upload, comma-separated, each by " + OpenModuleForms + " Empty: every module open in Service Studio on the environment.")],
            a => Studio.UploadModules(Text(a, "host"), Text(a, "modules")),
            ReadOnly: false),
        new("createApplication",
            "Creates an empty application on the server, as Service Studio's application page does: name, kind, and optionally description, primary colour " +
            "and icon. It has no modules: createModule adds them. A server change: call it only when the user asks for the application. A Service application " +
            "gets Service Studio's default Service template; the others none, so modules created in them are built plain.",
            [Host,
                new("name", "string", "The application's name; no other application may have it.", Required: true),
                new("kind", "string", "Reactive, Mobile (Phone), Traditional, Service or ReactiveLibrary. A module's kind has to fit its application's (createModule).", Required: true),
                new("description", "string", "The application's description."),
                new("primaryColor", "string", "The primary colour, #RRGGBB."),
                new("icon", "string", "Absolute path to a PNG or JPEG file, up to 1 MB.")],
            a => Studio.CreateApplication(Text(a, "host"), Required(a, "name"), Required(a, "kind"), Text(a, "description"), Text(a, "primaryColor"), Text(a, "icon")),
            ReadOnly: false),
        new("updateApplication",
            "Changes an application on the server, as Service Studio's Edit Application does: its name, description, primary colour or icon, and its kind. " +
            "Service Studio converts an application only to Service, and only once every module the server has in it is a Service module or an extension: " +
            "convert each module (convertModule) and publish it first. A server change: call it only when the user asks. Returns the application before and after.",
            [Host,
                new("application", "string", "The application's current name (exact).", Required: true),
                new("name", "string", "A new name."),
                new("description", "string", "A new description."),
                new("primaryColor", "string", "A new primary colour, #RRGGBB."),
                new("icon", "string", "Absolute path to a PNG or JPEG file, up to 1 MB."),
                new("kind", "string", "Service: converts the application to a Service application. No other kind is supported.")],
            a => Studio.UpdateApplication(Text(a, "host"), Required(a, "application"), Text(a, "name"), Text(a, "description"), Text(a, "primaryColor"), Text(a, "icon"), Text(a, "kind")),
            ReadOnly: false),
        new("createModule",
            "Creates a module in an application, as Service Studio's Create Module dialog does, and opens it in a new Service Studio tab, unsaved: it reaches the " +
            "server once published (omlPublish, when the user asks). The application's page comes to the front while it does. A module's kind has to fit its " +
            "application's: the application's own kind (blank, or from its template with template), a blank Service module in any application, a ReactiveLibrary " +
            "in a Reactive, Phone or Library application. Module names are unique in the environment.",
            [Host,
                new("application", "string", "The application to create it in (exact name).", Required: true),
                new("name", "string", "The module's name.", Required: true),
                new("kind", "string", "Reactive, Mobile (Phone), Traditional, Service or ReactiveLibrary. Default: the application's kind."),
                new("template", "boolean", "Build it from the application's template (screens, theme) instead of blank; only for the application's own kind.")],
            a => Studio.CreateModule(Text(a, "host"), Required(a, "application"), Required(a, "name"), Text(a, "kind"), Flag(a, "template")),
            ReadOnly: false),
        new("cloneModule",
            "Clones a module as Service Studio's Module › Clone does: a copy with a new module key, under the name given, opened in a new Service Studio tab, " +
            "unsaved. Nothing reaches the server: it's created there once published (omlPublish, when the user asks), with its own tables for its entities. " +
            "The source is the module open in Service Studio, as its tab has it, or else its published version. The clone publishes into the application " +
            "given, or the source's; a module's kind has to fit its application's (createModule). Module names are unique in the environment.",
            [Host,
                new("module", "string", "The module to clone: open in Service Studio (" + OpenModuleForms.TrimEnd('.') + "), or else published (its name or key).", Required: true),
                new("name", "string", "The clone's name.", Required: true),
                new("application", "string", "The application the clone publishes into (exact name). Default: the source's.")],
            a => Studio.CloneModule(Text(a, "host"), Required(a, "module"), Required(a, "name"), Text(a, "application")),
            ReadOnly: false),
        new("moveModule",
            "Moves a module into another application, as the module's Move on Service Studio's application page does: a server change, at once (the module " +
            "itself doesn't change, and nothing is published). Call it only when the user asks for that move. Service Studio's limits apply: not into Independent " +
            "Modules or a system application, nor out of a system application; an extension or Service module goes in any application, a Library in a Reactive, " +
            "Phone or Library one, any other module in one of its own kind; and not an application's last module, whose move would delete the application. A module " +
            "open but never published isn't on the server: for it, only the application its first publish creates it in changes. Returns the application before and after.",
            [Host,
                new("module", "string", "The module to move: open in Service Studio (" + OpenModuleForms.TrimEnd('.') + "), or else on the server (its name or key).", Required: true),
                new("application", "string", "The application to move it into (exact name).", Required: true)],
            a => Studio.MoveModule(Text(a, "host"), Required(a, "module"), Required(a, "application")),
            ReadOnly: false),
        new("convertModule",
            "Converts a module open in Service Studio to another kind, as Module › Convert does: Traditional, Reactive or Phone to Service, or to ReactiveLibrary " +
            "(which conversions apply comes from Service Studio). Service Studio closes the module's tab and reopens it converted, unsaved; the bridge confirms the " +
            "conversion and answers Service Studio's \"publish now?\" with No. It refuses a module with errors, elements the target kind can't hold (listed), or an " +
            "edit session of Service Studio's own server. Not to Reactive: Phone→Reactive also changes the application on the server and publishes.",
            [Host,
                new("module", "string", "The module open in Service Studio: " + OpenModuleForms, Required: true),
                new("to", "string", "Service or ReactiveLibrary.", Required: true)],
            a => Studio.ConvertModule(Text(a, "host"), Required(a, "module"), Required(a, "to")),
            ReadOnly: false),
        new("setReferenceTenantIdentifier",
            "Sets Show Tenant Identifier on a module's reference to another module's multi-tenant entity, as the entity's properties in Service Studio do " +
            "(the Model API only reads it on a reference). The setting is the consuming module's own: the producer is untouched. The open module gets one " +
            "unsaved, undoable change; nothing is saved or published. It refuses while Service Studio's own server has an edit session or unmerged changes " +
            "on the module, whose next omlMerge would put the old value back: set it before startOmlEditSession. Returns the value before and after.",
            [Host,
                new("module", "string", "The module open in Service Studio that holds the reference: " + OpenModuleForms, Required: true),
                new("producer", "string", "The referenced module, as the module's dependencies name it.", Required: true),
                new("entity", "string", "The referenced entity's name.", Required: true),
                new("value", "boolean", "true to show the tenant identifier, false to hide it.", Required: true)],
            a => Studio.SetReferenceTenantIdentifier(Text(a, "host"), Required(a, "module"), Required(a, "producer"), Required(a, "entity"), RequiredFlag(a, "value")),
            ReadOnly: false),
        new("layoutFlows",
            "Lays out the logic flows of a module open in Service Studio: every node on Service Studio's grid, and comment notes wrapped and placed. " +
            "The changed positions go into the module as one unsaved, undoable step (Layout Flows); nothing is saved or published. " +
            "It rearranges flows a developer drew too, unless flows names some. It refuses while Service Studio's own server has an edit session " +
            "or unmerged changes on the module, whose next omlMerge would put the old positions back: run it after the session's last merge and " +
            "endOmlEditSession. dryRun says what would move and changes nothing.",
            [Host, new("module", "string", "The module open in Service Studio: " + OpenModuleForms, Required: true),
                new("flows", "string", "Flows to lay out, by path or name, comma-separated (\"/Customer_CreateOrUpdate\" or \"Customer_CreateOrUpdate\"). Empty: every logic flow."),
                new("dryRun", "boolean", "Say what would move and change nothing.")],
            a => Studio.LayoutFlows(Text(a, "host"), Required(a, "module"), Text(a, "flows"), Flag(a, "dryRun")),
            ReadOnly: false),
    ];

    public static JsonArray Definitions() => new(All.Select(t => (JsonNode?)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = Schema(t.Parameters),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = t.ReadOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = true,
        },
    }).ToArray());

    public static JsonObject Call(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new ArgumentException("The tool name is missing.");
        var tool = All.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"Unknown tool: {name}");
        var args = parameters?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            return Result(Json.Write(Json.ToNode(tool.Run(args))), isError: false);
        }
        catch (Exception ex)
        {
            var error = Reflect.Unwrap(ex);
            var expected = error is BridgeException or ArgumentException;
            if (!expected)
            {
                Log.Error($"{name} failed", error);
            }

            return Result(expected ? error.Message : $"{error.GetType().Name}: {error.Message}", isError: true);
        }
    }

    private static JsonObject Result(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Schema(Parameter[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        var required = parameters.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }

        return schema;
    }

    /// <summary>A string argument (numbers accepted as text); null when absent or blank.</summary>
    private static string? Text(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        JsonNode node => Json.Write(node),
    };

    private static string Required(JsonObject args, string name) =>
        Text(args, name) ?? throw new ArgumentException($"{name} is required.");

    private static int? Number(JsonObject args, string name) => Text(args, name) switch
    {
        null => null,
        var text when int.TryParse(text, out var number) => number,
        _ => throw new ArgumentException($"{name} must be a whole number."),
    };

    private static bool RequiredFlag(JsonObject args, string name) =>
        args[name] == null ? throw new ArgumentException($"{name} is required.") : Flag(args, name);

    /// <summary>A flag that may be left out: null then, so a tool can tell an explicit false from nothing.</summary>
    private static bool? OptionalFlag(JsonObject args, string name) => args[name] == null ? null : Flag(args, name);

    private static bool Flag(JsonObject args, string name) => args[name] switch
    {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) => parsed,
        _ => throw new ArgumentException($"{name} must be true or false."),
    };
}
