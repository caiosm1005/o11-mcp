using System.Text.Json.Nodes;

namespace ServiceStudioBridge;

internal static class Tools
{
    private sealed record Parameter(string Name, string Type, string Description, bool Required = false);

    /// <summary>ReadOnly false: the tool writes a file or changes what's open in Service Studio.</summary>
    private sealed record Tool(string Name, string Description, Parameter[] Parameters, Func<JsonObject, object?> Run, bool ReadOnly = true);

    private static readonly Parameter Host = new("host", "string", "Environment host name. Only needed when Service Studio is signed in to several environments; by default the bridge uses the one it's signed in to.");

    private static readonly Parameter Module = new("module", "string", "The module's name.", Required: true);

    private static readonly Parameter Limit = new("limit", "integer", "How many items to return at most (default 200, up to 2000). Results say the total and whether some were left out.");

    private static readonly Parameter Details = new("details", "boolean", "Also return descriptions and keys (longer results).");

    private static readonly Tool[] All =
    [
        new("status",
            "The bridge and Service Studio versions, each Service Studio window (its module, the environment it's signed in to, host and name, and the user), " +
            "Capabilities: whether openModule, addDependency, layoutFlows and the dialog watch work with this Service Studio build, " +
            "AnsweredPrompts: the missing-dependencies prompts the bridge closed after openModule, and Windows: Service Studio's open windows (title, kind, " +
            "whether the bridge counts it as a dialog). Call it first.",
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
            "Service Studio's Find Usages in all Modules starts from, as the server has it from the consumers' published versions. It lists modules, not " +
            "where in them the element is used. The element is found by its exact name across the environment; when several producers expose that name, " +
            "it lists them instead: pass the producer (and kind). The bridge reads the producer as open in Service Studio, or loads its published version " +
            "in memory (nothing is written). An extension's elements aren't covered.",
            [Host,
                new("element", "string", "The public element's exact name.", Required: true),
                new("producer", "string", "The module that exposes it (exact name), when several do."),
                new("kind", "string", "The element's kind, as searchPublicElements reports it (Action, Entity, Structure, WebBlock…), when one name has several kinds."),
                Limit],
            a => Studio.GetElementConsumers(Text(a, "host"), Required(a, "element"), Text(a, "producer"), Text(a, "kind"), Number(a, "limit"))),
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
            "Once it has loaded, the bridge cancels Service Studio's reference check (\"Getting references from …\") as its Cancel button does, or closes the " +
            "missing-dependencies prompt that follows (Manage Dependencies doesn't open); status lists either under AnsweredPrompts, and the references stay unchecked. " +
            "It returns once Service Studio starts opening the module: wait until Service Studio's own server lists it before working on it.",
            [Host, Module,
                new("version", "string", "\"published\" (the default), \"latest\" for the newest upload even if unpublished, or a version number from getModuleVersions.")],
            a => Studio.OpenModule(Text(a, "host"), Required(a, "module"), Text(a, "version")),
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
                new("module", "string", "The module open in Service Studio that gets the dependency.", Required: true),
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
            [Host, new("modules", "string", "Modules to upload, comma-separated. Empty: every module open in Service Studio on the environment.")],
            a => Studio.UploadModules(Text(a, "host"), Text(a, "modules")),
            ReadOnly: false),
        new("layoutFlows",
            "Lays out the logic flows of a module open in Service Studio: every node on Service Studio's grid, and comment notes wrapped and placed. " +
            "The changed positions go into the module as one unsaved, undoable step (Layout Flows); nothing is saved or published. " +
            "It rearranges flows a developer drew too, unless flows names some. It refuses while Service Studio's own server has an edit session " +
            "or unmerged changes on the module, whose next omlMerge would put the old positions back: run it after the session's last merge and " +
            "endOmlEditSession. dryRun says what would move and changes nothing.",
            [Host, Module,
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

    private static bool Flag(JsonObject args, string name) => args[name] switch
    {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) => parsed,
        _ => throw new ArgumentException($"{name} must be true or false."),
    };
}
