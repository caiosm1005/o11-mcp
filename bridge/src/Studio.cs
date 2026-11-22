using System.Reflection;
using System.Security.Cryptography;

namespace ServiceStudioBridge;

/// <summary>
/// Reads from the environment through Service Studio's own server connection — its login
/// and its services — the way its Open from environment and Manage Dependencies dialogs do.
/// Read calls only: the services also delete and move modules, which this class never calls.
/// </summary>
internal static class Studio
{
    private static readonly object CallGate = new();

    // Claude Code refuses tool results above about 25,000 tokens: long lists come in pages.
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    private sealed record Connection(object Provider, string? Host, string? DisplayName, string? Environment, string? User, string? Module);

    private sealed record VersionEntry(object Raw, int Version, int VersionId);

    public static object Status()
    {
        List<object> connections = [];
        string? error = null;
        try
        {
            connections = Connections()
                .Select(c => (object)new { c.Module, c.Host, c.DisplayName, c.Environment, c.User })
                .ToList();
        }
        catch (Exception ex)
        {
            error = Reflect.Unwrap(ex).Message;
        }

        return new
        {
            Bridge = Bridge.Version,
            ServiceStudio = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            Config.Load().Port,
            Connections = connections,
            Error = error,
        };
    }

    public static object ListApplications(string? host, string? filter, int? limit)
    {
        var connection = Pick(host);
        var applications = Applications(connection).Where(a => Matches(Reflect.TryGet(a, "Name") as string, filter)).ToList();

        // ListApplications leaves each application's module list empty: take them from the module list.
        var modulesByApplication = Modules(connection)
            .GroupBy(m => Reflect.TryGet(m, "ApplicationName") as string ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => Reflect.TryGet(m, "Name") as string).Order().ToList(), StringComparer.OrdinalIgnoreCase);

        return Scoped(connection, Page(applications
            .Select(a =>
            {
                var picked = Reflect.Pick(a, "Name", "Description", "ApplicationKind", "IsIsolated", "Key");
                picked["Modules"] = modulesByApplication.GetValueOrDefault(Reflect.TryGet(a, "Name") as string ?? string.Empty) ?? [];
                return picked;
            })
            .ToList(), limit, "narrow it with filter"));
    }

    public static object ListModules(string? host, string? filter, string? application, bool details, int? limit)
    {
        var connection = Pick(host);
        string[] fields = details
            ? ["Name", "Kind", "ESpaceKind", "ApplicationName", "Version", "IsActive", "UploadDate", "UploaderUsername", "Description", "Key", "ApplicationKey"]
            : ["Name", "Kind", "ESpaceKind", "ApplicationName", "Version", "IsActive", "UploadDate", "UploaderUsername"];
        return Scoped(connection, Page(Modules(connection)
            .Where(m => Matches(Reflect.TryGet(m, "Name") as string, filter))
            .Where(m => application == null || string.Equals(Reflect.TryGet(m, "ApplicationName") as string, application, StringComparison.OrdinalIgnoreCase))
            .Select(m => Reflect.Pick(m, fields))
            .ToList(), limit, "narrow it with filter or application"));
    }

    public static object GetModuleVersions(string? host, string module, int? limit)
    {
        var connection = Pick(host);
        var found = FindModule(connection, module);
        var versions = Versions(connection, found);
        var published = Published(versions);
        return Scoped(connection, new
        {
            Module = Reflect.TryGet(found, "Name"),
            PublishedVersion = published?.Version,
            Versions = Page(versions.Select(v =>
            {
                var picked = Reflect.Pick(v.Raw, "Version", "UploadDate", "UploaderUsername", "UploaderName", "CommitMessage");
                picked["Published"] = ReferenceEquals(v, published);
                return picked;
            }).ToList(), limit, "these are the newest"),
        });
    }

    public static object GetConsumers(string? host, string module)
    {
        var connection = Pick(host);
        var key = Reflect.Get(FindModule(connection, module), "Key");
        return Scoped(connection, Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetConsumers", key)))
            .Select(c => Reflect.Pick(c, "Name", "Key"))
            .ToList());
    }

    public static object SearchPublicElements(string? host, string? text, string? kind, string? producer, int? limit)
    {
        var connection = Pick(host);

        // The service takes text and kind filters but ignores them and returns every public element.
        var wantedKind = kind == null ? null : ElementKind(kind);
        return Scoped(connection, Page(Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetPublicElements", null, null, null)))
            .Where(e => Matches(Reflect.TryGet(e, "Name") as string, text))
            .Where(e => wantedKind == null || string.Equals(ElementKind(Reflect.TryGet(e, "Type")?.ToString()), wantedKind, StringComparison.OrdinalIgnoreCase))
            .Where(e => producer == null || string.Equals(Reflect.TryGet(e, "ApplicationName") as string, producer, StringComparison.OrdinalIgnoreCase))
            .Select(e => (object)new Dictionary<string, object?>
            {
                ["Name"] = Reflect.TryGet(e, "Name"),
                ["Kind"] = ElementKind(Reflect.TryGet(e, "Type")?.ToString()),
                // The service calls the producer module its "application".
                ["Producer"] = Reflect.TryGet(e, "ApplicationName"),
                ["ProducerType"] = Reflect.TryGet(e, "ApplicationType"),
                ["IsExtension"] = Reflect.TryGet(e, "IsExtension"),
                ["Description"] = Reflect.TryGet(e, "Description"),
                ["Key"] = Reflect.TryGet(e, "Key"),
            })
            .ToList(), limit, "narrow it with text, kind or producer"));
    }

    public static object GetApplicationDependencies(string? host, string application)
    {
        var connection = Pick(host);
        var app = Applications(connection).FirstOrDefault(a => string.Equals(Reflect.TryGet(a, "Name") as string, application, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeException($"No application named {application}.");
        var key = Reflect.Get(app, "Key");
        return Scoped(connection, Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetApplicationDependenciesDetails", key)))
            .Select(d => Reflect.Pick(d, "Name", "Description", "ApplicationKind", "IsIsolated", "Key"))
            .ToList());
    }

    public static object ListReferenceable(string? host, string module, string? filter, bool details, int? limit)
    {
        var connection = Pick(host);
        var found = FindModule(connection, module);
        var key = Reflect.Get(found, "Key");
        var applicationKey = Reflect.Get(found, "ApplicationKey");
        var references = Reflect.Get(connection.Provider, "ReferencesService")
            ?? throw new BridgeException("Service Studio has no references service for this connection.");
        string[] fields = details
            ? ["Name", "Kind", "RuntimeKind", "SignatureHash", "Description", "IsSystemExtension", "IsMobilePlugin", "Key", "AppKey"]
            : ["Name", "Kind", "RuntimeKind", "SignatureHash"];
        return Scoped(connection, Page(Run(() => Reflect.Enumerate(Reflect.Call(references, "ListReferenceable", key, applicationKey, CancellationToken.None)))
            .Where(r => Matches(Reflect.TryGet(r, "Name") as string, filter))
            .Select(r => Reflect.Pick(r, fields))
            .ToList(), limit, "narrow it with filter"));
    }

    public static object DownloadModule(string? host, string module, string? version, string? outputDir)
    {
        var directory = outputDir ?? Paths.Downloads;
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("outputDir must be an absolute path.");
        }

        var connection = Pick(host);
        var found = FindModule(connection, module);
        var name = Reflect.TryGet(found, "Name") as string ?? module;
        var key = Reflect.Get(found, "Key");
        var (selected, versionId, versions) = Select(connection, found, version);

        var downloads = Reflect.Get(connection.Provider, "DownloadService")
            ?? throw new BridgeException("Service Studio has no download service for this connection.");
        var data = Run(() => Reflect.Call(downloads, "DownloadESpace", versionId, key, CancellationToken.None), timeoutSeconds: 300);
        if (Reflect.TryGet(data, "Success") is false)
        {
            throw new BridgeException("The server refused the download: " + MessageText(Reflect.TryGet(data, "Message")));
        }

        if (Reflect.TryGet(data, "Data") is not byte[] { Length: > 0 } bytes)
        {
            throw new BridgeException("The server returned no module data.");
        }

        // The answer's Version echoes the id that was asked for: 0 for "published".
        var entry = selected ?? versions.FirstOrDefault(v => v.VersionId == ToInt(Reflect.TryGet(data, "Version")));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{Safe(name)}.{(entry != null ? "v" + entry.Version : "published")}.oml");
        File.WriteAllBytes(file, bytes);

        return Scoped(connection, new
        {
            Module = name,
            Path = file,
            Bytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            entry?.Version,
            entry?.VersionId,
            IsPublishedVersion = Reflect.TryGet(data, "IsPublishedVersion"),
            UploadedBy = Reflect.TryGet(data, "UploadUser"),
            UploadDate = Reflect.TryGet(data, "UploadDate"),
            Comment = entry == null ? null : Reflect.TryGet(entry.Raw, "CommitMessage"),
        });
    }

    private static IEnumerable<Connection> Connections()
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var runtime = Reflect.Static(runtimeType, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");

        // One server connection per Service Studio window; windows may share one.
        var presenters = Reflect.Enumerate(Reflect.TryGet(runtime, "Aggregators"))
            .Concat(Reflect.Enumerate(Reflect.TryCall(runtime, "GetAllTopLevelPresenters")));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var presenter in presenters)
        {
            var provider = Reflect.TryGet(presenter, "ServerCommunicationsProvider");
            if (provider == null || !seen.Add(provider))
            {
                continue;
            }

            // Only the host and user name are read from the login: never its secret.
            var login = Reflect.TryGet(provider, "LoginInfo");
            var hostInfo = Reflect.TryGet(login, "HostInfo");
            yield return new Connection(
                provider,
                Reflect.TryGet(hostInfo, "HostName") as string,
                Reflect.TryGet(hostInfo, "DisplayName") as string,
                Reflect.TryGet(hostInfo, "HostEnvironmentType")?.ToString(),
                Reflect.TryGet(login, "UserName") as string,
                Reflect.TryGet(Reflect.TryGet(presenter, "OwnerESpace"), "Name") as string);
        }
    }

    /// <summary>The environment Service Studio is signed in to; when it's signed in to several, the one named by host.</summary>
    private static Connection Pick(string? host)
    {
        var signedIn = Connections().Where(c => !string.IsNullOrEmpty(c.Host) && !string.IsNullOrEmpty(c.User)).ToList();
        if (signedIn.Count == 0)
        {
            throw new BridgeException("Service Studio isn't signed in to any environment: sign in there first.");
        }

        var hosts = signedIn.Select(c => c.Host!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (host != null)
        {
            return signedIn.FirstOrDefault(c => string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException($"Service Studio isn't signed in to {host}; it's signed in to {string.Join(", ", hosts)}.");
        }

        if (hosts.Count > 1)
        {
            throw new BridgeException($"Service Studio is signed in to several environments ({string.Join(", ", hosts)}): pass host to pick one.");
        }

        return signedIn[0];
    }

    /// <summary>Every result says which environment it came from.</summary>
    private static object Scoped(Connection connection, object result) => new
    {
        Environment = new { connection.Host, connection.DisplayName, Type = connection.Environment, connection.User },
        Result = result,
    };

    private static object ApplicationService(Connection connection) =>
        Reflect.Get(connection.Provider, "ApplicationManagementService")
        ?? throw new BridgeException("Service Studio has no application service for this connection.");

    private static List<object> Applications(Connection connection) =>
        Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "ListApplications")));

    private static List<object> Modules(Connection connection) =>
        Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetModules")));

    private static object FindModule(Connection connection, string name)
    {
        var modules = Modules(connection);
        var exact = modules.Where(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            var which = exact.Select(m => $"{Reflect.TryGet(m, "Kind")} in {Reflect.TryGet(m, "ApplicationName")}");
            throw new BridgeException($"More than one module is named {name} ({string.Join("; ", which)}); the bridge can't tell them apart yet.");
        }

        var close = modules
            .Select(m => Reflect.TryGet(m, "Name") as string)
            .Where(n => n != null && n.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        throw new BridgeException($"No module named {name}." + (close.Count > 0 ? $" Close matches: {string.Join(", ", close)}." : string.Empty));
    }

    private static List<VersionEntry> Versions(Connection connection, object module)
    {
        var key = Reflect.Get(module, "Key");
        return Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetModuleVersions", key, default(DateTime))))
            .Select(v => new VersionEntry(v, ToInt(Reflect.TryGet(v, "Version")), ToInt(Reflect.TryGet(v, "VersionId"))))
            .OrderByDescending(v => v.Version)
            .ToList();
    }

    /// <summary>
    /// The published version: the one entry flagged active (Service Studio sets it from the module's
    /// published version id). None when the module has never been published. The module list's own
    /// VersionId isn't a substitute: on servers still on the SOAP API it's the latest upload.
    /// </summary>
    private static VersionEntry? Published(List<VersionEntry> versions)
    {
        var active = versions.Where(v => Reflect.TryGet(v.Raw, "IsActive") is true).ToList();
        return active.Count == 1 ? active[0] : null;
    }

    /// <summary>
    /// Resolves "published", "latest" or a version number. Choices such as "someone's last upload
    /// after the published version" are the agent's to make from getModuleVersions' list.
    /// </summary>
    private static (VersionEntry? Entry, int VersionId, List<VersionEntry> Versions) Select(Connection connection, object module, string? selector)
    {
        var choice = string.IsNullOrWhiteSpace(selector) ? "published" : selector.Trim().ToLowerInvariant();
        var versions = Versions(connection, module);
        switch (choice)
        {
            case "published":
                // 0 lets the server pick the published version itself. With nothing published it
                // would quietly send the latest upload instead, so refuse.
                var published = Published(versions)
                    ?? throw new BridgeException($"{Reflect.TryGet(module, "Name")} has no published version: pass version \"latest\" or a number from getModuleVersions.");
                return (published, 0, versions);

            case "latest":
                var latest = versions.FirstOrDefault() ?? throw new BridgeException("The module has no versions.");
                return (latest, latest.VersionId, versions);

            default:
                if (!int.TryParse(choice, out var number))
                {
                    throw new ArgumentException("version must be \"published\", \"latest\" or a version number from getModuleVersions.");
                }

                var entry = versions.FirstOrDefault(v => v.Version == number)
                    ?? throw new BridgeException($"The module has no version {number}; getModuleVersions lists them.");
                return (entry, entry.VersionId, versions);
        }
    }

    /// <summary>Runs one server call at a time, with a timeout, and surfaces the real exception.</summary>
    private static T Run<T>(Func<T> body, int timeoutSeconds = 120)
    {
        if (!Monitor.TryEnter(CallGate, TimeSpan.FromSeconds(timeoutSeconds)))
        {
            throw new BridgeException("Another bridge call is still running; try again.");
        }

        try
        {
            var task = Task.Run(body);
            if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                throw new BridgeException($"Service Studio didn't answer within {timeoutSeconds} s.");
            }

            return task.Result;
        }
        catch (AggregateException ex)
        {
            throw Reflect.Unwrap(ex);
        }
        finally
        {
            Monitor.Exit(CallGate);
        }
    }

    /// <summary>The first items of a long list, with the total and a hint on narrowing it.</summary>
    private static object Page<T>(List<T> items, int? limit, string narrow)
    {
        var max = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        return new
        {
            Total = items.Count,
            Returned = Math.Min(items.Count, max),
            Note = items.Count > max ? $"Only the first {max} of {items.Count}: {narrow}, or raise limit (up to {MaxLimit})." : null,
            Items = items.Take(max).ToList(),
        };
    }

    /// <summary>"ReferenceAction" and "Action" are the same kind; results use the short form.</summary>
    private static string? ElementKind(string? kind) =>
        kind != null && kind.StartsWith("Reference", StringComparison.Ordinal) && kind.Length > "Reference".Length ? kind["Reference".Length..] : kind;

    private static bool Matches(string? value, string? filter) =>
        filter == null || (value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static int ToInt(object? value)
    {
        try
        {
            return value is IConvertible ? Convert.ToInt32(value) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string MessageText(object? message) =>
        new[] { "Text", "Message", "Description" }.Select(n => Reflect.TryGet(message, n) as string).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? message?.ToString()
        ?? "no reason given";

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
