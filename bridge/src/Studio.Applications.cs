using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ServiceStudioBridge;

/// <summary>
/// Creates and edits applications on the server the way Service Studio's application page does:
/// ApplicationCommands.CreateApplicationFromDetails and EditApplication, both public, which end in
/// the application service's CreateOrUpdateApplication and return its error instead of showing it.
/// </summary>
internal static partial class Studio
{
    /// <summary>
    /// Module and application kinds by the names Service Studio's own MCP server gives them
    /// (moduleType), with Service Studio's SegmentationKind for each.
    /// </summary>
    private static readonly (string Name, string Segmentation)[] Kinds =
    [
        ("Reactive", "CrossDevice"),
        ("Mobile", "Mobile"),
        ("Traditional", "WebResponsive"),
        ("Service", "Service"),
        ("ReactiveLibrary", "ReactiveLibrary"),
        ("TraditionalLibrary", "WebResponsiveLibrary"),
        ("MobileLibrary", "MobileLibrary"),
    ];

    /// <summary>The kinds an application can be created with.</summary>
    private static readonly string[] ApplicationKinds = ["Reactive", "Mobile", "Traditional", "Service", "ReactiveLibrary"];

    private static Type SegmentationKindType() =>
        Reflect.FindType("OutSystems.MetaModel.Enumerations.SegmentationKind", "OutSystems.MetaModel");

    /// <summary>A kind argument by its moduleType name: a moduleType name, or Service Studio's own, any case.</summary>
    private static string KindArgument(string kind, string argument, IReadOnlyCollection<string> allowed)
    {
        var known = Kinds.FirstOrDefault(k => k.Name.Equals(kind, StringComparison.OrdinalIgnoreCase)
            || k.Segmentation.Equals(kind, StringComparison.OrdinalIgnoreCase));
        return known.Name != null && allowed.Contains(known.Name)
            ? known.Name
            : throw new ArgumentException($"{argument} must be one of {string.Join(", ", allowed)}.");
    }

    /// <summary>A kind (moduleType name) as Service Studio's SegmentationKind.</summary>
    private static object Segmentation(string kind) =>
        Enum.Parse(SegmentationKindType(), Kinds.First(k => k.Name == kind).Segmentation);

    /// <summary>A SegmentationKind (or a nullable one) by its moduleType name.</summary>
    private static string KindName(object? kind) =>
        Kinds.FirstOrDefault(k => k.Segmentation == kind?.ToString()).Name ?? kind?.ToString() ?? "Unknown";

    private static string? ParseColor(string? color) => color switch
    {
        null => null,
        _ when Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") => color,
        _ => throw new ArgumentException("primaryColor must be a #RRGGBB colour, like #1068EB."),
    };

    /// <summary>An icon argument: an absolute path to a PNG or JPEG file, read whole.</summary>
    private static byte[]? ReadIcon(string? path)
    {
        if (path == null)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("icon must be an absolute path to a PNG or JPEG file.");
        }

        if (!File.Exists(path))
        {
            throw new BridgeException($"There's no file at {path}.");
        }

        var bytes = File.ReadAllBytes(path);
        var png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        var jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (!png && !jpeg)
        {
            throw new BridgeException($"{path} isn't a PNG or JPEG image.");
        }

        return bytes.Length <= 1024 * 1024 ? bytes : throw new BridgeException($"{path} is over 1 MB: use a smaller icon.");
    }

    /// <summary>The application page of the window the connection belongs to (IServerPagePresenter), which Service Studio creates applications and modules through.</summary>
    private static object ServerPage(Connection connection)
    {
        var window = Reflect.Call(connection.Presenter, "GetOwnerAggregatorWindow") ?? throw new BridgeException("Service Studio's window isn't there.");
        return Reflect.Get(window, "ServerPagePresenter") ?? throw new BridgeException("Service Studio's window has no application page.");
    }

    private static object FindApplication(Connection connection, string name) =>
        Applications(connection).FirstOrDefault(a => string.Equals(Reflect.TryGet(a, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new BridgeException($"No application named {name}. listApplications lists them.");

    /// <summary>A MessageDefinition as one line: its title and its text.</summary>
    private static string DefinitionText(object definition) =>
        string.Join(": ", new[] { Reflect.TryGet(definition, "Message") as string, Reflect.TryGet(definition, "Detail") as string }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private sealed record ApplicationsApi(MethodInfo Create, MethodInfo Edit, object ServiceTemplate, object OthersApplication);

    private static ApplicationsApi? applicationsApi;

    /// <summary>The public members createApplication and updateApplication use.</summary>
    private static ApplicationsApi ApplicationMembers()
    {
        if (applicationsApi != null)
        {
            return applicationsApi;
        }

        var commands = Reflect.FindType("ServiceStudio.Presenter.Commands.ApplicationCommands", "ServiceStudio.Presenter");
        var segmentation = SegmentationKindType();
        var create = commands.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == "CreateApplicationFromDetails" && m.GetParameters() is { Length: 8 } p && p[1].ParameterType == segmentation)
            ?? throw new BridgeException("ApplicationCommands.CreateApplicationFromDetails(presenter, kind, …) is missing");
        var edit = commands.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m => m.Name == "EditApplication" && m.GetParameters().Length == 4)
            ?? throw new BridgeException("ApplicationCommands.EditApplication is missing");
        var constants = Reflect.FindType("OutSystems.RuntimeCommon.Constants", "OutSystems.RuntimeCommon");
        object Constant(string name) => constants.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new BridgeException($"Constants.{name} is missing");
        var info = Reflect.FindType("ServiceStudio.ServerCommunications.API.ApplicationInfo", "ServiceStudio.ServerCommunications.API");
        if (info.GetMethod("Clone", Type.EmptyTypes) == null
            || new[] { "Name", "Description", "PrimaryColor", "Icon", "ApplicationKind", "TemplateKey", "Modules" }.Any(f => info.GetField(f) == null)
            || Reflect.FindType("ServiceStudio.Presenter.IServerPagePresenter", "ServiceStudio.Presenter").GetMethod("GetApplicationInfoFromServer") == null)
        {
            throw new BridgeException("a member of the application record the bridge edits is missing");
        }

        return applicationsApi = new ApplicationsApi(create, edit, Constant("ServiceDefaultTemplateESpaceKey"), Constant("OthersApplicationKey"));
    }

    /// <summary>An application as the server has it now, as Service Studio's application page reads it.</summary>
    private static object ApplicationRecord(Connection connection, object serverPage, object application)
    {
        var key = Reflect.Get(application, "Key");
        var name = Reflect.TryGet(application, "Name") as string;
        return Run(() => Reflect.Call(serverPage, "GetApplicationInfoFromServer", key, name, false))
            ?? throw new BridgeException($"The server returned nothing for the application {name}.");
    }

    public static object CreateApplication(string? host, string name, string kind, string? description, string? primaryColor, string? iconPath)
    {
        var kindName = KindArgument(kind, "kind", ApplicationKinds);
        var color = ParseColor(primaryColor);
        var icon = ReadIcon(iconPath);
        var connection = Pick(host);
        var api = ApplicationMembers();
        var kindValue = Segmentation(kindName);
        if (Applications(connection).Any(a => string.Equals(Reflect.TryGet(a, "Name") as string, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BridgeException($"An application named {name} exists already.");
        }

        if (Reflect.TryGet(connection.Provider, "TemplatesService") is { } templates
            && Reflect.TryCall(templates, "IsSegmentationKindSupported", kindValue) is false)
        {
            throw new BridgeException($"This server doesn't take {KindName(kindValue)} applications.");
        }

        // No template, as asked: a Service application gets the one Service Studio's own conversion
        // to Service sets, the others none (new modules in them are then built plain).
        var template = kindName == "Service" ? api.ServiceTemplate : null;
        var serverPage = ServerPage(connection);
        object? message = null;
        object? key = null;
        InContext(serverPage, $"Creating the application {name}", () =>
        {
            object?[] arguments = [serverPage, kindValue, null, name, description ?? string.Empty, icon, color, template];
            message = api.Create.Invoke(null, arguments);
            key = arguments[2];
        }, wait: true);
        if (message != null)
        {
            throw new BridgeException($"Service Studio couldn't create {name}: {DefinitionText(message)}");
        }

        var created = Applications(connection).FirstOrDefault(a => KeyId(Reflect.TryGet(a, "Key")) == KeyId(key));
        return Scoped(connection, new
        {
            Application = Reflect.TryGet(created, "Name") ?? name,
            Kind = KindName(Reflect.TryGet(created, "ApplicationKind") ?? kindValue),
            Key = KeyId(key),
            Note = "Created on the server, with no modules: createModule adds them."
                + (created == null ? " The application list doesn't show it yet: Service Studio may still be refreshing it." : string.Empty),
        });
    }

    public static object UpdateApplication(string? host, string application, string? name, string? description, string? primaryColor, string? iconPath, string? kind)
    {
        if (name == null && description == null && primaryColor == null && iconPath == null && kind == null)
        {
            throw new ArgumentException("Pass at least one of name, description, primaryColor, icon or kind.");
        }

        var color = ParseColor(primaryColor);
        var icon = ReadIcon(iconPath);
        var kindName = kind == null ? null : KindArgument(kind, "kind", ApplicationKinds);
        if (kindName is not (null or "Service"))
        {
            throw new ArgumentException($"Service Studio converts an application only to Service, not to {kindName}: an application becomes Reactive "
                + "only together with its Phone module (Module › Convert in Service Studio), and none becomes a Library.");
        }

        var connection = Pick(host);
        var api = ApplicationMembers();
        var kindValue = kindName == null ? null : Segmentation(kindName);
        var found = FindApplication(connection, application);
        var appName = Reflect.TryGet(found, "Name") as string ?? application;
        if (name != null && !name.Equals(appName, StringComparison.OrdinalIgnoreCase)
            && Applications(connection).Any(a => string.Equals(Reflect.TryGet(a, "Name") as string, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BridgeException($"Another application is named {name} already.");
        }

        var serverPage = ServerPage(connection);
        var current = ApplicationRecord(connection, serverPage, found);
        if (kindValue != null)
        {
            CheckConvertibleToService(current, appName, api);
        }

        var edited = Reflect.Call(current, "Clone") ?? throw new BridgeException("Service Studio couldn't copy the application record.");
        if (name != null)
        {
            SetMember(edited, "Name", name);
        }

        if (description != null)
        {
            SetMember(edited, "Description", description);
        }

        if (color != null)
        {
            SetMember(edited, "PrimaryColor", color);
        }

        if (icon != null)
        {
            SetMember(edited, "Icon", icon);
        }

        if (kindValue != null)
        {
            // As the application page's Convert to Service does.
            SetMember(edited, "ApplicationKind", kindValue);
            SetMember(edited, "TemplateKey", api.ServiceTemplate);
        }

        object? message = null;
        InContext(serverPage, $"Updating the application {appName}", () =>
        {
            try
            {
                message = api.Edit.Invoke(null, [serverPage, edited, false, false]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                throw new BridgeException($"{appName} is a system application: Service Studio doesn't edit it.");
            }
        }, wait: true);
        if (message != null)
        {
            throw new BridgeException($"Service Studio couldn't update {appName}: {DefinitionText(message)}");
        }

        var after = ApplicationRecord(connection, serverPage, Applications(connection).FirstOrDefault(a => KeyId(Reflect.TryGet(a, "Key")) == KeyId(Reflect.TryGet(found, "Key"))) ?? found);
        object Summary(object record) => new
        {
            Name = Reflect.TryGet(record, "Name"),
            Description = Reflect.TryGet(record, "Description"),
            PrimaryColor = Reflect.TryGet(record, "PrimaryColor"),
            Kind = KindName(Reflect.TryGet(record, "ApplicationKind")),
            // The server re-encodes icons: two of the same size and colour count can come out the same
            // length, so the hash tells them apart.
            Icon = Reflect.TryGet(record, "Icon") is byte[] { Length: > 0 } icon
                ? $"{icon.Length} bytes, sha256 {Convert.ToHexString(SHA256.HashData(icon))[..12].ToLowerInvariant()}"
                : null,
        };
        return Scoped(connection, new { Application = appName, Before = Summary(current), After = Summary(after) });
    }

    /// <summary>
    /// Service Studio converts an application only to Service, and only when it isn't the Others
    /// application or a system one, isn't Service or a Library already, and every module the server
    /// has in it is a Service module or an extension.
    /// </summary>
    private static void CheckConvertibleToService(object record, string appName, ApplicationsApi api)
    {
        var kind = KindName(Reflect.TryGet(record, "ApplicationKind"));
        if (KeyId(Reflect.TryGet(record, "Key")) == KeyId(api.OthersApplication))
        {
            throw new BridgeException($"{appName} is the Others application, which keeps its kind.");
        }

        if (kind is "Service" or "ReactiveLibrary")
        {
            throw new BridgeException($"{appName} is a {kind} application already.");
        }

        var blocking = Reflect.Enumerate(Reflect.TryGet(record, "Modules"))
            .Where(m => Reflect.TryGet(m, "Kind")?.ToString() != "Extension" && KindName(Reflect.TryGet(m, "ESpaceKind")) != "Service")
            .Select(m => $"{Reflect.TryGet(m, "Name")} ({KindName(Reflect.TryGet(m, "ESpaceKind"))})")
            .ToList();
        if (blocking.Count > 0)
        {
            var shown = string.Join(", ", blocking.Take(15)) + (blocking.Count > 15 ? $", and {blocking.Count - 15} more" : string.Empty);
            throw new BridgeException($"{appName} has {blocking.Count} modules that aren't Service modules on the server: {shown}. "
                + "Convert each with convertModule and publish it, then convert the application.");
        }
    }
}
