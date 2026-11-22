using System.Text.Json.Nodes;

namespace ServiceStudioBridge;

internal static class Tools
{
    private sealed record Parameter(string Name, string Type, string Description, bool Required = false);

    private sealed record Tool(string Name, string Description, Parameter[] Parameters, Func<JsonObject, object?> Run, bool WritesFiles = false);

    private static readonly Parameter Host = new("host", "string", "Environment host name. Only needed when Service Studio is signed in to several environments; by default the bridge uses the one it's signed in to.");

    private static readonly Parameter Module = new("module", "string", "The module's name.", Required: true);

    private static readonly Parameter Limit = new("limit", "integer", "How many items to return at most (default 200, up to 2000). Results say the total and whether some were left out.");

    private static readonly Parameter Details = new("details", "boolean", "Also return descriptions and keys (longer results).");

    private static readonly Tool[] All =
    [
        new("status",
            "The bridge and Service Studio versions, and each Service Studio window: its module, the environment it's signed in to (host, name, type) and the user. Call it first.",
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
            "The modules that reference (consume) a module.",
            [Host, Module],
            a => Studio.GetConsumers(Text(a, "host"), Required(a, "module"))),
        new("searchPublicElements",
            "Searches the public elements that modules in the environment expose, as Manage Dependencies lists them: name, kind and producer module.",
            [Host,
                new("text", "string", "Case-insensitive part of the element name."),
                new("kind", "string", "Only this kind, such as Action, ServiceAPIMethod, Entity, StaticEntity, Structure, WebBlock or Theme (a \"Reference\" prefix is optional)."),
                new("producer", "string", "Only elements of this producer module (exact name)."),
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
            WritesFiles: true),
    ];

    public static JsonArray Definitions() => new(All.Select(t => (JsonNode?)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = Schema(t.Parameters),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = !t.WritesFiles,
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
