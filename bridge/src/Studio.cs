using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ServiceStudioBridge;

/// <summary>
/// Works with the environment through Service Studio's own server connection — its login
/// and its services — the way its Open from environment and Manage Dependencies dialogs do.
/// It opens modules and adds dependencies to open ones through Service Studio's own commands,
/// never saves or publishes, and never calls the services that delete or move modules.
/// </summary>
internal static partial class Studio
{
    private static readonly object CallGate = new();

    // Claude Code refuses tool results above about 25,000 tokens: long lists come in pages.
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    private sealed record Connection(object Provider, string? Host, string? DisplayName, string? User, string? Module, object Presenter);

    private sealed record VersionEntry(object Raw, int Version, int VersionId);

    public static object Status()
    {
        List<object> connections = [];
        string? error = null;
        try
        {
            connections = Connections()
                .Select(c => (object)new { c.Module, c.Host, c.DisplayName, c.User })
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
            Capabilities = Capabilities(),
            AnsweredPrompts = Answered.ToArray(),
            Windows = connections.Count > 0 ? WindowList() : null,
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
            }).ToList(), limit, null, newest: true),
        });
    }

    public static object GetConsumers(string? host, string module, int? limit)
    {
        var connection = Pick(host);
        var modules = Modules(connection);
        var key = Reflect.Get(FindModule(connection, module, modules), "Key");
        return Scoped(connection, Page(ConsumerRows(Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetConsumers", key))), modules), limit, null));
    }

    /// <summary>
    /// The server's consumer list, sorted by name, each with its application: impact is judged per
    /// application, which the list leaves out, so it comes from the module list.
    /// </summary>
    private static List<Dictionary<string, object?>> ConsumerRows(List<object> consumers, List<object> modules)
    {
        var applicationByKey = new Dictionary<string, string?>();
        foreach (var m in modules)
        {
            var id = KeyId(Reflect.TryGet(m, "Key"));
            if (id != null)
            {
                applicationByKey.TryAdd(id, Reflect.TryGet(m, "ApplicationName") as string);
            }
        }

        return consumers
            .Select(c =>
            {
                var picked = Reflect.Pick(c, "Name", "Key");
                picked["Application"] = applicationByKey.GetValueOrDefault(KeyId(Reflect.TryGet(c, "Key")) ?? string.Empty);
                return picked;
            })
            .OrderBy(c => c.GetValueOrDefault("Name") as string, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Opens a module in Service Studio as Open from environment does: Service Studio downloads it
    /// into a new tab itself, so no file is written. Returns once Service Studio has started.
    /// Without a version, Service Studio picks it, as Open from environment does: the module list's
    /// entry, which is the published version (the latest upload when none is published). With one,
    /// the list entry is copied with that version's fields, which is what Open from environment's
    /// version picker hands the same command.
    /// </summary>
    public static object OpenModule(string? host, string module, string? version)
    {
        var connection = Pick(host);
        var found = FindModule(connection, module);
        var name = Reflect.TryGet(found, "Name") as string ?? module;
        var (wanted, _, versions) = version == null ? (null, 0, (List<VersionEntry>?)null) : Select(connection, found, version);

        var tab = OpenTab(connection, name);
        if (tab != null)
        {
            // ESpace.Version is the version id the tab was opened from (0 for a module never uploaded).
            var openId = ToInt(Reflect.TryGet(Reflect.TryGet(tab, "OwnerESpace"), "Version"));
            var openVersion = (versions ?? Versions(connection, found)).FirstOrDefault(v => v.VersionId == openId)?.Version;
            if (wanted != null && wanted.VersionId != openId)
            {
                throw new BridgeException($"{name} is already open in Service Studio, on {(openVersion is { } n ? $"version {n}" : "a version the server doesn't list")}, "
                    + $"not version {wanted.Version}: close its tab in Service Studio, then call openModule again.");
            }

            return Scoped(connection, new { Module = name, Status = "alreadyOpen", Version = openVersion });
        }

        var (open, published) = OpenFromEnvironment();
        var info = found;
        var source = published;
        if (wanted != null)
        {
            info = RuntimeHelpers.GetObjectValue(found)!;   // a copy of the boxed ModuleInfo
            foreach (var field in new[] { "VersionId", "Version", "IsActive", "UploadDate", "UploaderUsername", "UploaderName", "CommitMessage" })
            {
                SetMember(info, field, Reflect.Get(wanted.Raw, field), required: field is "VersionId" or "Version");
            }

            if (Reflect.TryGet(wanted.Raw, "IsActive") is not true)
            {
                source = Enum.Parse(published.GetType(), "OtherVersion");
            }
        }

        var window = Reflect.TryGet(connection.Presenter, "TopLevelPresenter") ?? connection.Presenter;

        // forceAggregator: a tab of its own, opened in the background, like the File menu's command.
        InContext(window, $"Opening {name}", () => open.Invoke(null, [window, info, true, source, null]), wait: false);
        WatchForMissingDependencies(name, connection.Host, Reflect.TryGet(found, "Key")?.ToString());
        return Scoped(connection, new
        {
            Module = name,
            Status = "opening",
            Version = wanted?.Version ?? ToInt(Reflect.TryGet(found, "Version")),
            Note = "Service Studio is downloading it into a new tab. Wait until Service Studio's own server lists it among the open modules before working on it. "
                + "The bridge cancels the reference check Service Studio runs once it has loaded (\"Getting references from …\"), and closes the "
                + "messages that check shows in the module's tab (the missing-dependencies prompt, a communication error, \"not logged in\") "
                + "as their close button does; status lists each under AnsweredPrompts, with what it said.",
        });
    }

    /// <summary>How a module holds another module's element.</summary>
    private enum Holding
    {
        /// <summary>Not at all.</summary>
        None,

        /// <summary>
        /// Only as a hidden stub: an element, most often an entity, that came in with something that
        /// uses it, such as the type of a referenced action's parameter. It is Hidden, an entity has
        /// only its identifier, and the module can't use it: Manage Dependencies shows it unticked.
        /// </summary>
        Stub,

        /// <summary>Ticked: the module can use it.</summary>
        Ticked,
    }

    /// <summary>
    /// How the open module holds the producer's element, read through the public Model API
    /// (OutSystems.Model, the API applyModelApiCode runs): an element under a reference carries a
    /// global key made of the producer's module key and the element's own key. Service Studio's
    /// IGlobalKey only serializes (newer Model APIs also split it), so its text must contain a form
    /// of each key: the ObjectKey text or the GUID. Every reference is searched, not only the one
    /// named after the producer: the keys alone identify the element, and the reference's name can
    /// differ from the producer's, as (System) does from ServiceCenter (SystemProducer). The key reads
    /// "producerKey*elementKey", and an element's children extend it ("…*elementKey.#IdentifierType"),
    /// so the part after the "*" must be the element's key itself. A match whose Hidden is true is a
    /// stub, not a reference.
    /// </summary>
    private static Holding HowReferenced(object eSpace, object producerKey, object elementKey)
    {
        var espaceInterface = eSpace.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == "OutSystems.Model.IESpace")
            ?? throw new BridgeException("the module doesn't implement the Model API's IESpace");
        var signature = espaceInterface.Assembly.GetType("OutSystems.Model.IObjectSignature")
            ?? throw new BridgeException("OutSystems.Model.IObjectSignature is missing");
        var descendants = (signature.GetMethod("GetAllDescendantsOfType") ?? throw new BridgeException("IObjectSignature.GetAllDescendantsOfType is missing"))
            .MakeGenericMethod(signature);
        var globalKey = GlobalKeyProperty(signature);
        var owner = globalKey.DeclaringType ?? signature;
        var serialize = espaceInterface.Assembly.GetType("OutSystems.Model.IGlobalKey")?.GetMethod("SerializeToString", Type.EmptyTypes);
        string Text(object? key) => key == null ? string.Empty : (serialize?.Invoke(key, null) as string ?? key.ToString() ?? string.Empty);
        string[] Forms(object key) => new[] { key.ToString(), KeyId(key) }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToArray();
        var producerForms = Forms(producerKey);
        var elementForms = Forms(elementKey);

        var references = Reflect.Enumerate(
            (new[] { espaceInterface }.Concat(espaceInterface.GetInterfaces()).Select(t => t.GetProperty("References")).FirstOrDefault(p => p != null)
                ?? throw new BridgeException("IESpace.References is missing")).GetValue(eSpace));
        var stub = false;
        foreach (var reference in references)
        {
            foreach (var item in Reflect.Enumerate(descendants.Invoke(reference, null)).Where(owner.IsInstanceOfType))
            {
                var text = Text(globalKey.GetValue(item));
                var star = text.LastIndexOf('*');
                var own = star < 0 ? text : text[(star + 1)..];
                if (producerForms.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase))
                    && elementForms.Any(f => string.Equals(own, f, StringComparison.OrdinalIgnoreCase)))
                {
                    // Every element Service Studio puts under a reference (ServiceStudio.Model's
                    // Reference* classes) has a public Hidden, from ITopLevelReferenceElement; the
                    // V1 interfaces don't declare it in this build. Anything without it reads null.
                    if (Reflect.TryGet(item, "Hidden") is true)
                    {
                        stub = true;
                        continue;
                    }

                    return Holding.Ticked;
                }
            }
        }

        return stub ? Holding.Stub : Holding.None;
    }

    /// <summary>
    /// Service Studio's own MCP server keeps its edit sessions in an IOmlSessionState singleton in its
    /// service container, keyed by the module's InstanceId (ServiceStudio.Mcp isn't obfuscated).
    /// </summary>
    private static (Type Host, FieldInfo App, Type State) McpSessionStateMembers()
    {
        var host = Reflect.FindType("ServiceStudio.Mcp.Host.McpServerHost", "ServiceStudio.Mcp");
        var app = host.GetField("app", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new BridgeException("McpServerHost.app is missing");
        var state = Reflect.FindType("ServiceStudio.Mcp.Oml.IOmlSessionState", "ServiceStudio.Mcp");
        _ = state.GetMethod("GetOrCreate") ?? throw new BridgeException("IOmlSessionState.GetOrCreate is missing");
        return (host, app, state);
    }

    /// <summary>
    /// Whether Service Studio's own MCP server has an edit session open, or unmerged changes, on this
    /// module; null, with the reason, when this build doesn't let the bridge see it.
    /// </summary>
    private static bool? EditSessionOn(object eSpace, out string? problem)
    {
        problem = null;
        try
        {
            var (hostType, appField, stateType) = McpSessionStateMembers();
            var host = hostType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (host == null || appField.GetValue(host) is not { } app)
            {
                return false;   // the MCP server hasn't started: it has no sessions
            }

            var services = Reflect.Get(app, "Services") as IServiceProvider ?? throw new BridgeException("the MCP server has no service container");
            var state = services.GetService(stateType) ?? throw new BridgeException("the MCP server has no session state");
            var id = Reflect.Get(eSpace, "InstanceId") ?? throw new BridgeException("the module has no InstanceId");
            // GetOrCreate is what every one of its tools calls; a new entry is empty and closed.
            var entry = Reflect.Call(state, "GetOrCreate", id);
            return Reflect.TryGet(entry, "IsOpen") is true || Reflect.TryGet(entry, "HasUnmergedChanges") is true;
        }
        catch (Exception ex)
        {
            problem = Reflect.Unwrap(ex).Message;
            return null;
        }
    }

    /// <summary>The GlobalKey property, on IObjectSignature or its interfaces, else on IModelObject.</summary>
    private static PropertyInfo GlobalKeyProperty(Type signature)
    {
        PropertyInfo? Find(Type? type) =>
            type == null ? null : new[] { type }.Concat(type.GetInterfaces()).Select(t => t.GetProperty("GlobalKey")).FirstOrDefault(p => p != null);
        var modelObject = signature.Assembly.GetType("OutSystems.Model.IModelObject")
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("OutSystems.Model.IModelObject")).FirstOrDefault(t => t != null);
        return Find(signature) ?? Find(modelObject) ?? throw new BridgeException("the Model API's GlobalKey is missing");
    }

    /// <summary>The Model API members HowReferenced reads (OutSystems.Model.V1), for status to check without a module.</summary>
    private static void ReferenceCheckMembers()
    {
        var espace = Reflect.FindType("OutSystems.Model.IESpace", "OutSystems.Model.V1");
        Type ModelType(string name) => espace.Assembly.GetType(name) ?? throw new BridgeException($"{name} is missing");
        bool Has(Type type, string property) => new[] { type }.Concat(type.GetInterfaces()).Any(t => t.GetProperty(property) != null);
        _ = GlobalKeyProperty(ModelType("OutSystems.Model.IObjectSignature"));
        if (!Has(espace, "References")
            || ModelType("OutSystems.Model.IObjectSignature").GetMethod("GetAllDescendantsOfType") == null
            || ModelType("OutSystems.Model.IGlobalKey").GetMethod("SerializeToString", Type.EmptyTypes) == null
            || Reflect.FindType("ServiceStudio.Model.Interfaces.ITopLevelReferenceElement", "ServiceStudio.Framework").GetProperty("Hidden") == null)
        {
            throw new BridgeException("a Model API member addDependency reads to check the reference is missing");
        }
    }

    /// <summary>What openModule needs beyond the command: another version's source, the ModuleInfo fields it sets, and the open module's version id.</summary>
    private static void OpenModuleMembers()
    {
        var (open, published) = OpenFromEnvironment();
        _ = Enum.Parse(published.GetType(), "OtherVersion");
        var moduleInfo = open.GetParameters()[1].ParameterType;
        if (moduleInfo.GetField("VersionId") == null || moduleInfo.GetField("Version") == null
            || Reflect.FindType("ServiceStudio.Model.ESpace", "ServiceStudio.Framework").GetProperty("Version") == null)
        {
            throw new BridgeException("a member openModule reads to pick or report a version is missing");
        }
    }

    /// <summary>Open from environment's public command, DownloadAndOpen.OpenModuleFromEnvironment, and the source it reports.</summary>
    private static (MethodInfo Open, object Source) OpenFromEnvironment() =>
        (Reflect.FindType("ServiceStudio.Presenter.Commands.DownloadAndOpen", "ServiceStudio.Presenter")
                .GetMethod("OpenModuleFromEnvironment", BindingFlags.Public | BindingFlags.Static)
                ?? throw new BridgeException("DownloadAndOpen.OpenModuleFromEnvironment is missing"),
            Enum.Parse(Reflect.FindType("ServiceStudio.Framework.ESpaceOpenedFromSource", "ServiceStudio.Framework"), "PublishedVersion"));

    /// <summary>
    /// Whether this Service Studio build has what the tools that drive it need: "ok", or what's
    /// missing. Service Studio updates itself, so status says it before a tool fails.
    /// </summary>
    private static object Capabilities()
    {
        static string Check(Action resolve)
        {
            try
            {
                resolve();
                return "ok";
            }
            catch (Exception ex)
            {
                return Reflect.Unwrap(ex).Message;
            }
        }

        var (api, problem) = ManageDependencies();
        string addDependency;
        if (api != null)
        {
            addDependency = "ok";
        }
        else
        {
            var fallback = Check(() => AddDependencyRoutine());
            addDependency = fallback == "ok"
                ? $"ok, through the fallback routine (the public route changed in this build: {problem})"
                : $"unavailable in this Service Studio build ({problem}; fallback: {fallback})";
        }

        return new
        {
            OpenModule = Check(OpenModuleMembers),
            AddDependency = addDependency,
            // addDependency refuses while Service Studio's MCP server has a session on the module.
            EditSessionCheck = Check(() => McpSessionStateMembers()),
            // addDependency reads the module before and after, so it never reports a dependency it didn't add.
            ReferenceCheck = Check(ReferenceCheckMembers),
            LayoutFlows = Check(() => LayoutMembers()),
            // openModule closes the missing-dependencies prompt, and waits name the dialog in their way.
            DialogWatch = Check(DialogMembers),
            // uploadModules runs Service Studio's own Upload command.
            Upload = Check(UploadMembers),
            // getElementConsumers loads the producer and asks the server about one of its elements.
            ElementConsumers = Check(() => ElementConsumersMembers()),
            // With countUsages or part, it searches each consumer as Find Usages in all Modules does.
            UsageCount = Check(() => UsageMembers()),
            // createApplication and updateApplication run Service Studio's application commands.
            Applications = Check(() => ApplicationMembers()),
            // createModule runs the Create Module dialog's commands; cloneModule what Module › Clone runs; convertModule the Module › Convert ones.
            CreateModule = Check(() => CreateModuleMembers()),
            CloneModule = Check(() => CloneModuleMembers()),
            // moveModule makes the server call the application page's Move makes.
            MoveModule = Check(() => MoveModuleMembers()),
            ConvertModule = Check(() => ConvertModuleMembers()),
            // setReferenceTenantIdentifier sets a property of Service Studio's own model object for a referenced entity.
            ReferenceTenantIdentifier = Check(() => ReferenceEntityMembers()),
        };
    }

    /// <summary>
    /// Adds a dependency on a producer's public element to a module open in Service Studio, as
    /// Search in other modules › Add dependency does, with the same steps. The change is an unsaved
    /// edit of the open module (one undoable "Add Dependency" command): nothing is saved or published.
    /// </summary>
    public static object AddDependency(string? host, string module, string element, string? producer, string? kind)
    {
        var (connection, tab, name) = ResolveOpenModule(host, module);
        if (tab == null)
        {
            throw NotOpen(module, connection);
        }

        module = name;
        var eSpace = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");
        var moduleKey = KeyId(Reflect.TryGet(eSpace, "Key"));

        // Service Studio's own MCP server merges a working copy with a two-way Compare-and-Merge
        // that keeps the working copy's side: a dependency added to the live module while one is
        // checked out would be merged away. One added before startOmlEditSession is in its snapshot.
        string? sessionWarning = null;
        switch (EditSessionOn(eSpace, out var sessionProblem))
        {
            case true:
                throw new BridgeException($"Service Studio's MCP server has an edit session (or unmerged changes) on {module}. "
                    + "Its omlMerge keeps the working copy's side, which doesn't have this dependency, so the next merge would remove it. "
                    + "Merge what the session holds (omlMerge), end it (endOmlEditSession), call addDependency again, then start a new "
                    + "session: its snapshot includes the dependency.");
            case null:
                sessionWarning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}). If one is open, "
                    + "its next omlMerge removes this dependency: end it and start a new one.";
                break;
        }

        var matches = PublicElementsNamed(connection, element, producer, kind)
            .Where(e => KeyId(Reflect.TryGet(e, "ApplicationKey")) != moduleKey)
            .ToList();
        if (matches.Count == 0)
        {
            throw new BridgeException($"No other module exposes a public element named {element}"
                + (kind != null ? $" of kind {kind}" : string.Empty) + (producer != null ? $" in {producer}" : string.Empty)
                + ". searchPublicElements finds names that are close.");
        }

        if (matches.Count > 1)
        {
            throw new BridgeException($"{matches.Count} public elements match: {string.Join("; ", matches.Select(DescribeElement))}. "
                + "Ask the user which one, then pass its producer (and kind, if one producer has several).");
        }

        var match = matches[0];
        var producerName = ProducerName(match) ?? string.Empty;
        var label = $"Adding {element} from {producerName} to {module}";
        var producerKey = Reflect.Get(match, "ApplicationKey") ?? throw new BridgeException("The public element has no producer key.");
        var elementKey = Reflect.Get(match, "Key") ?? throw new BridgeException("The public element has no key.");
        var warnings = new List<string>();
        if (sessionWarning != null)
        {
            warnings.Add(sessionWarning);
        }

        // The module is read before and after through the public Model API: Service Studio's routine
        // can report an element as added that the module can't use (a client action in a Service
        // module), which it then leaves out. A hidden stub counts as not referenced, both ways.
        string? verifyProblem = null;
        Holding? Referenced()
        {
            try
            {
                return HowReferenced(eSpace, producerKey, elementKey);
            }
            catch (Exception ex)
            {
                verifyProblem = Reflect.Unwrap(ex).Message;
                return null;
            }
        }

        Holding? before = null, after = null;

        // Outcome codes, as the fallback routine's enum declares them: added, element not found, broken dependencies, not public.
        int outcome;
        var (api, problem) = ManageDependencies();
        if (api != null)
        {
            var added = false;
            InContext(tab, label, () =>
            {
                before = Referenced();
                if (before == Holding.Ticked)
                {
                    return;
                }

                added = AddThroughManageDependencies(api, tab, eSpace, producerKey, elementKey);
                after = Referenced();
            }, wait: true);
            outcome = added ? 0 : 1;
        }
        else
        {
            Log.Info($"addDependency: the public route isn't there in this Service Studio build ({problem}); using the fallback routine");
            var routine = AddDependencyRoutine();
            var elementType = routine.GetParameters()[2].ParameterType.GetGenericArguments()[0];
            var request = Activator.CreateInstance(elementType)!;
            // Keys as Service Studio's own dialog passes them: ObjectKey text, parsed back on its side.
            SetMember(request, "elementKey", Reflect.Get(match, "Key")?.ToString());
            SetMember(request, "producerKey", Reflect.Get(match, "ApplicationKey")?.ToString());
            SetMember(request, "name", Reflect.TryGet(match, "Name") as string, required: false);
            // The service's own name for the producer (ServiceCenter, not (System)), as the routine received it before.
            SetMember(request, "producerName", Reflect.TryGet(match, "ApplicationName") as string, required: false);
            var requests = Array.CreateInstance(elementType, 1);
            requests.SetValue(request, 0);

            object? result = null;
            InContext(tab, label, () =>
            {
                before = Referenced();
                if (before == Holding.Ticked)
                {
                    return;
                }

                result = routine.Invoke(null, [tab, eSpace, requests]);
                after = Referenced();
            }, wait: true);
            outcome = result == null ? -1 : Convert.ToInt32(result);
        }

        if (before == Holding.Ticked)
        {
            return Scoped(connection, new
            {
                Module = module,
                AlreadyReferenced = DescribeElement(match),
                Note = "Nothing changed: the module already references it.",
                Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null,
            });
        }

        if (outcome == 0 && after == Holding.Stub)
        {
            throw new BridgeException($"Service Studio took the request, but {module} still holds {DescribeElement(match)} only as a hidden stub "
                + "(it came in as the type of something the module uses, such as a referenced action's parameter), so the module can't use it. "
                + "Tick it in Manage Dependencies, and tell the bridge's maintainers this build needs another way to add it.");
        }

        if (outcome == 0 && after == Holding.None)
        {
            throw new BridgeException($"Service Studio took the request, but {module} doesn't reference {DescribeElement(match)} afterwards, so nothing was added: "
                + $"{module} can't use that element (a Service or library module can't reference client actions, screens or blocks, for one). "
                + "Choose another element, or try it in Manage Dependencies to see Service Studio's reason.");
        }

        if (outcome == 0 && after == null)
        {
            warnings.Add($"The bridge couldn't check that {module} now references it ({verifyProblem}): look in Manage Dependencies.");
        }

        return outcome switch
        {
            0 => Scoped(connection, new
            {
                Module = module,
                Added = DescribeElement(match),
                Note = (before == Holding.Stub ? "It was only a hidden stub before (no attributes or records); now it's ticked. " : string.Empty)
                    + "Unsaved in Service Studio. Start the edit session now: its snapshot includes this dependency, and omlMerge keeps it.",
                Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null,
            }),
            1 => throw new BridgeException($"Service Studio couldn't find {DescribeElement(match)} among what {module} can reference."),
            2 => throw new BridgeException($"Service Studio couldn't add {DescribeElement(match)}: the producer's dependencies are broken. Fix or refresh them in Manage Dependencies."),
            3 => throw new BridgeException($"{DescribeElement(match)} isn't a public element {module} can use."),
            var other => throw new BridgeException($"Service Studio's add-dependency routine answered {other}, which the bridge doesn't know. Check Manage Dependencies in Service Studio."),
        };
    }

    public static object SearchPublicElements(string? host, string? text, string? kind, string? producer, int? limit)
    {
        var connection = Pick(host);

        // The service takes text and kind filters but ignores them and returns every public element.
        var wantedKind = kind == null ? null : ElementKind(kind);
        return Scoped(connection, Page(Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetPublicElements", null, null, null)))
            .Where(e => Matches(Reflect.TryGet(e, "Name") as string, text))
            .Where(e => wantedKind == null || string.Equals(ElementKind(Reflect.TryGet(e, "Type")?.ToString()), wantedKind, StringComparison.OrdinalIgnoreCase))
            .Where(e => FromProducer(e, producer))
            .Select(e => (object)new Dictionary<string, object?>
            {
                ["Name"] = Reflect.TryGet(e, "Name"),
                ["Kind"] = ElementKind(Reflect.TryGet(e, "Type")?.ToString()),
                // The service calls the producer module its "application".
                ["Producer"] = ProducerName(e),
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
        var (data, bytes) = Run(() => Download(connection, versionId, key!), timeoutSeconds: 300);

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

    /// <summary>
    /// A module's OML from the server, as Open from environment downloads it: the answer (its version
    /// and upload fields) and the file's bytes. Version id 0 asks for the published version.
    /// </summary>
    private static (object Data, byte[] Bytes) Download(Connection connection, int versionId, object key)
    {
        var downloads = Reflect.Get(connection.Provider, "DownloadService")
            ?? throw new BridgeException("Service Studio has no download service for this connection.");
        var data = Reflect.Call(downloads, "DownloadESpace", versionId, key, CancellationToken.None)
            ?? throw new BridgeException("The server returned no answer to the download.");
        if (Reflect.TryGet(data, "Success") is false)
        {
            throw new BridgeException("The server refused the download: " + MessageText(Reflect.TryGet(data, "Message")));
        }

        if (Reflect.TryGet(data, "Data") is not byte[] { Length: > 0 } bytes)
        {
            throw new BridgeException("The server returned no module data.");
        }

        return (data, bytes);
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
                Reflect.TryGet(login, "UserName") as string,
                Reflect.TryGet(Reflect.TryGet(presenter, "OwnerESpace"), "Name") as string,
                presenter);
        }
    }

    /// <summary>The tab (aggregator) that has the module open, in a window signed in to the connection's environment.</summary>
    private static object? OpenTab(Connection connection, string module)
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var runtime = Reflect.Static(runtimeType, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        return Reflect.Enumerate(Reflect.TryGet(runtime, "Aggregators")).FirstOrDefault(a =>
            string.Equals(Reflect.TryGet(Reflect.TryGet(a, "OwnerESpace"), "Name") as string, module, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(a, "ServerCommunicationsProvider"), "LoginInfo"), "HostInfo"), "HostName") as string,
                connection.Host,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The host a tab's window is signed in to.</summary>
    private static string? TabHost(object tab) =>
        Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(tab, "ServerCommunicationsProvider"), "LoginInfo"), "HostInfo"), "HostName") as string;

    /// <summary>A module key (ObjectKey) as a Guid (AsGuid), the form Service Studio's own server compares keys in.</summary>
    private static Guid? KeyGuid(object? key) =>
        Reflect.TryGet(key, "AsGuid") as Guid? ?? (Guid.TryParse(key?.ToString(), out var parsed) ? parsed : null);

    private static Guid? ModuleGuid(object? eSpace) => KeyGuid(Reflect.TryGet(eSpace, "Key"));

    /// <summary>A tab's id as Service Studio's own server's listOpenModules gives it: the tab (aggregator) id, then the module's key, 32 hex digits.</summary>
    private static string? TabId(object tab) =>
        Reflect.TryGet(tab, "Id") is { } id && ModuleGuid(Reflect.TryGet(tab, "OwnerESpace")) is { } key ? $"{id}/{key:N}" : null;

    /// <summary>
    /// The tabs a module argument names, as Service Studio's own server resolves eSpaceName: the tab
    /// with that tabId; else the tabs holding the module with that key (with or without dashes);
    /// else those holding a module of that name (here in any case, as the bridge always took names).
    /// </summary>
    private static List<object> MatchTabs(IEnumerable<object> tabs, string module)
    {
        var value = module.Trim();
        var all = tabs.Where(t => Reflect.TryGet(t, "OwnerESpace") != null).ToList();
        var byTab = all.Where(t => string.Equals(TabId(t), value, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        if (byTab.Count > 0)
        {
            return byTab;
        }

        var byKey = Guid.TryParse(value, out var key) ? all.Where(t => ModuleGuid(Reflect.TryGet(t, "OwnerESpace")) == key).ToList() : [];
        return byKey.Count > 0
            ? byKey
            : all.Where(t => string.Equals(Reflect.TryGet(Reflect.TryGet(t, "OwnerESpace"), "Name") as string, value, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The refusal for a module argument several open tabs match, naming each with its tabId.</summary>
    private static BridgeException Ambiguous(string module, IEnumerable<object> tabs) =>
        new($"Several open modules match '{module}': pass the tabId of the one to work on as module. Candidates: "
            + string.Join(", ", tabs.Select(t => $"{Reflect.TryGet(Reflect.TryGet(t, "OwnerESpace"), "Name")} (tabId '{TabId(t)}', on {TabHost(t)})")) + ".");

    /// <summary>The refusal for a module argument no open tab matches.</summary>
    private static BridgeException NotOpen(string module, Connection connection) =>
        new($"{module} isn't open in Service Studio (signed in to {connection.Host}). Open it first."
            + (module.Contains('/') ? " A tabId stops matching once its tab closes or holds another module: Service Studio's listOpenModules gives the current ones." : string.Empty));

    /// <summary>
    /// The open module a tool acts on, from its tabId, key or name (see MatchTabs), with the
    /// environment it's open on: host's, or else the tab's own window's, so a tabId or key also picks
    /// the environment. Name is the module's name once found (the argument otherwise), for messages.
    /// Tab is null when nothing open matches; several matches are refused, listing their tabIds.
    /// </summary>
    private static (Connection Connection, object? Tab, string Name) ResolveOpenModule(string? host, string module)
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var runtime = Reflect.Static(runtimeType, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        var tabs = Reflect.Enumerate(Reflect.TryGet(runtime, "Aggregators"))
            .Where(t => TabHost(t) is { } tabHost && (host == null || string.Equals(tabHost, host, StringComparison.OrdinalIgnoreCase)));
        var matches = MatchTabs(tabs, module);
        if (matches.Count > 1)
        {
            throw Ambiguous(module, matches);
        }

        if (matches.Count == 0)
        {
            return (Pick(host), null, module);
        }

        var tab = matches[0];
        return (Pick(host ?? TabHost(tab)), tab, Reflect.TryGet(Reflect.TryGet(tab, "OwnerESpace"), "Name") as string ?? module);
    }

    /// <summary>
    /// Runs body on the presenter's own thread, once Service Studio is idle, as its commands run.
    /// With wait, blocks until body finishes and rethrows what it threw. A modal dialog stops the
    /// queue: when one has been open for a few seconds and body hasn't started, it fails at once,
    /// naming the dialog. The timeout stays under an MCP client's own (about a minute), so the
    /// caller gets the reason rather than a bare timeout.
    /// </summary>
    private static void InContext(object presenter, string what, Action body, bool wait, int timeoutSeconds = 50)
    {
        const int Queued = 0, Started = 1, Abandoned = 2;
        var state = Queued;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action action = () =>
        {
            try
            {
                // Reported as failed already: doing it now would surprise whoever asked.
                if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued)
                {
                    Log.Info($"{what}: skipped, the caller had given up waiting");
                    return;
                }

                body();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                var error = Reflect.Unwrap(ex);
                Log.Error($"{what} failed", error);
                done.TrySetException(error);
            }
        };
        Reflect.Call(presenter, "ExecuteInContext", action, false, true);
        if (!wait)
        {
            return;
        }

        var started = DateTime.UtcNow;
        try
        {
            while (!done.Task.Wait(TimeSpan.FromSeconds(1)))
            {
                var waited = DateTime.UtcNow - started;
                var dialogs = waited >= TimeSpan.FromSeconds(6) && Volatile.Read(ref state) == Queued ? TryOpenDialogs() : null;
                if (dialogs is { Count: > 0 } && Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued)
                {
                    throw new BridgeException($"Service Studio has a dialog open ({string.Join("; ", dialogs.Select(d => $"\"{d.Title}\""))}), "
                        + "and it runs nothing else until someone answers it. Answer it in Service Studio (or ask the user to), then try again.");
                }

                if (waited >= TimeSpan.FromSeconds(timeoutSeconds))
                {
                    if (Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued)
                    {
                        throw new BridgeException($"Service Studio didn't get to it within {timeoutSeconds} s, with no dialog the bridge could see: it may be busy. Try again shortly.");
                    }

                    var asking = TryOpenDialogs() is { Count: > 0 } open ? $", showing {string.Join("; ", open.Select(d => $"\"{d.Title}\""))}" : string.Empty;
                    throw new BridgeException($"Service Studio is still at it after {timeoutSeconds} s{asking}; it carries on (once anything it asks is answered), and the result will show in Service Studio.");
                }
            }
        }
        catch (AggregateException ex)
        {
            throw Reflect.Unwrap(ex);
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
        Environment = new { connection.Host, connection.DisplayName, connection.User },
        Result = result,
    };

    private static object ApplicationService(Connection connection) =>
        Reflect.Get(connection.Provider, "ApplicationManagementService")
        ?? throw new BridgeException("Service Studio has no application service for this connection.");

    private static List<object> Applications(Connection connection) =>
        Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "ListApplications")));

    private static List<object> Modules(Connection connection) =>
        Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetModules")));

    private static object FindModule(Connection connection, string name, List<object>? modules = null)
    {
        modules ??= Modules(connection);
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

    /// <summary>
    /// The first items of a long list, with the total and how to get the rest: narrow, when the tool
    /// takes filters, says how ("narrow it with filter"); newest says the list runs newest first.
    /// </summary>
    private static object Page<T>(List<T> items, int? limit, string? narrow, bool newest = false)
    {
        var max = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var shown = newest ? $"The {max} newest of {items.Count}" : $"The first {max} of {items.Count}";
        var more = narrow == null ? $"raise limit (up to {MaxLimit}) for more" : $"{narrow}, or raise limit (up to {MaxLimit}) for more";
        return new
        {
            Total = items.Count,
            Returned = Math.Min(items.Count, max),
            Note = items.Count > max ? $"{shown}; {more}." : null,
            Items = items.Take(max).ToList(),
        };
    }

    /// <summary>A key as the text results show for it (a global key's GUID), to match keys across services.</summary>
    private static string? KeyId(object? key) => key == null ? null : Json.ToNode(key)?.ToString();

    /// <summary>
    /// The public-elements service names the producer of the platform's own elements (ListAppend,
    /// Site_Property, User…) after the module that implements them, ServiceCenter. Manage
    /// Dependencies, listReferenceable and a module's references call that producer (System), and
    /// list no ServiceCenter: the bridge shows (System) too, and takes either name as a producer.
    /// </summary>
    private const string SystemProducer = "(System)";
    private const string SystemModule = "ServiceCenter";

    /// <summary>The environment's public elements with this exact name, of the kind and producer asked for (any, when null).</summary>
    private static List<object> PublicElementsNamed(Connection connection, string element, string? producer, string? kind)
    {
        var wantedKind = kind == null ? null : ElementKind(kind);
        return Run(() => Reflect.Enumerate(Reflect.Call(ApplicationService(connection), "GetPublicElements", null, null, null)))
            .Where(e => string.Equals(Reflect.TryGet(e, "Name") as string, element, StringComparison.OrdinalIgnoreCase))
            .Where(e => wantedKind == null || string.Equals(ElementKind(Reflect.TryGet(e, "Type")?.ToString()), wantedKind, StringComparison.OrdinalIgnoreCase))
            .Where(e => FromProducer(e, producer))
            .ToList();
    }

    /// <summary>A public element as results name it: "Name (Kind) in Producer".</summary>
    private static string DescribeElement(object e) => $"{Reflect.TryGet(e, "Name")} ({ElementKind(Reflect.TryGet(e, "Type")?.ToString())}) in {ProducerName(e)}";

    /// <summary>A public element's producer as Manage Dependencies names it: (System) for ServiceCenter's elements.</summary>
    private static string? ProducerName(object element)
    {
        var name = Reflect.TryGet(element, "ApplicationName") as string;
        return string.Equals(name, SystemModule, StringComparison.OrdinalIgnoreCase) ? SystemProducer : name;
    }

    /// <summary>Whether a public element comes from the producer asked for (any, when null); (System) and ServiceCenter name the same one.</summary>
    private static bool FromProducer(object element, string? producer) =>
        producer == null
        || string.Equals(ProducerName(element), producer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Reflect.TryGet(element, "ApplicationName") as string, producer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The public Service Studio members addDependency uses. Public names survive Service Studio's
    /// obfuscation, which renames only internal and private members, and its own MCP server relies on
    /// ReferencesManager and Command the same way, so these change far less often between builds.
    /// </summary>
    private sealed record ManageDependenciesApi(
        ConstructorInfo NewManager,
        MethodInfo InitializeAddReferences,
        MethodInfo Commit,
        ConstructorInfo NewPresenter,
        Func<object> NewView,
        object ShowAll,
        MethodInfo ExecuteReadOnly);

    private static (ManageDependenciesApi? Api, string? Problem)? manageDependencies;

    /// <summary>Resolves the public route once; Problem says what this Service Studio build lacks.</summary>
    private static (ManageDependenciesApi? Api, string? Problem) ManageDependencies()
    {
        if (manageDependencies is { } known)
        {
            return known;
        }

        try
        {
            var presenterAssembly = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter").Assembly;
            Type Need(string name) => presenterAssembly.GetType(name) ?? throw new BridgeException($"{name} is missing");
            var managerType = Need("ServiceStudio.Presenter.AddRemoveReferences.ReferencesManager");
            var presenterType = Need("ServiceStudio.Presenter.AddRemoveReferencesPresenter");
            var viewInterface = Reflect.FindType("ServiceStudio.View.IAddRemoveReferencesView", "ServiceStudio.View");
            var providerInterface = Reflect.FindType("ServiceStudio.View.IViewImplementationProvider", "ServiceStudio.View");
            var providerType = Reflect.FindType("ServiceStudio.View.ViewImplementationProvider", "ServiceStudio.View");
            var createView = (providerInterface.GetMethod("CreateView", Type.EmptyTypes)
                ?? throw new BridgeException("IViewImplementationProvider.CreateView is missing")).MakeGenericMethod(viewInterface);
            var command = Reflect.FindType("ServiceStudio.Commands.Command", "ServiceStudio.Framework");
            MethodInfo Method(Type type, string name) =>
                type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new BridgeException($"{type.Name}.{name} is missing");

            var api = new ManageDependenciesApi(
                // ReferencesManager(ITopLevelPresenter, ESpace, bool skipInitialization, CancellationToken, IApplicationInformationService, IReferencesService, DecorationCache)
                managerType.GetConstructors().Single(c => c.GetParameters() is { Length: 7 } p && p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(CancellationToken)),
                Method(managerType, "InitializeAddReferences"),
                Method(managerType, "CommitRefreshReferences"),
                // AddRemoveReferencesPresenter(string id, IAggregatorPresenter, IAddRemoveReferencesView, ReferencesManager, ObjectKey initialReferenceKey, bool withLoadedRefs, ReferenceListMode)
                presenterType.GetConstructors().Single(c => c.GetParameters() is { Length: 7 } p && p[0].ParameterType == typeof(string) && p[2].ParameterType == viewInterface),
                () => createView.Invoke(Reflect.Static(providerType, "Instance") ?? throw new BridgeException("Service Studio has no view provider yet."), null)!,
                Enum.Parse(Reflect.FindType("ServiceStudio.View.ReferenceListMode", "ServiceStudio.View"), "ShowAll"),
                command.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == "ExecuteReadOnly" && m.GetParameters() is { Length: 5 } p && p[2].ParameterType == typeof(Action)));
            known = (api, null);
        }
        catch (Exception ex)
        {
            known = (null, Reflect.Unwrap(ex).Message);
        }

        manageDependencies = known;
        return known;
    }

    /// <summary>
    /// Service Studio's Add dependency, step by step: load the producer into a ReferencesManager, tick
    /// the element in a Manage Dependencies presenter that's never shown, and commit, which is one
    /// undoable command. False when the producer or the element isn't among what the module can reference.
    /// </summary>
    private static bool AddThroughManageDependencies(ManageDependenciesApi api, object tab, object eSpace, object producerKey, object elementKey)
    {
        var manager = api.NewManager.Invoke([tab, eSpace, true, CancellationToken.None, null, null, null]);
        try
        {
            var producers = Array.CreateInstance(producerKey.GetType(), 1);
            producers.SetValue(producerKey, 0);
            api.InitializeAddReferences.Invoke(manager, [CancellationToken.None, producers]);
            var presenter = api.NewPresenter.Invoke(["AddRemoveReferences", tab, api.NewView(), manager, null, true, api.ShowAll]);
            try
            {
                var ticked = false;
                Action tick = () =>
                {
                    var producer = Reflect.Call(presenter, "FindProducerMatch", producerKey);
                    if (producer == null)
                    {
                        return;
                    }

                    var tree = Reflect.Get(presenter, "ReferenceTreePresenter") ?? throw new BridgeException("The Manage Dependencies presenter has no tree.");
                    // AddRoots takes the matches themselves or their tree folders: the overload for the match.
                    var addRoots = tree.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).First(m =>
                        m.Name == "AddRoots"
                        && m.GetParameters() is [{ ParameterType: { IsGenericType: true } list }]
                        && list.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                        && list.GetGenericArguments()[0].IsInstanceOfType(producer));
                    var roots = Array.CreateInstance(addRoots.GetParameters()[0].ParameterType.GetGenericArguments()[0], 1);
                    roots.SetValue(producer, 0);
                    addRoots.Invoke(tree, [roots]);

                    var target = Reflect.Call(presenter, "FindMatchElementByKey", producer, elementKey);
                    if (target != null)
                    {
                        Reflect.Call(presenter, "SetIsChecked", target, true);
                        ticked = true;
                    }
                };
                api.ExecuteReadOnly.Invoke(null, [Reflect.Call(tab, "GetPresenterContext"), "Add Dependency", tick, false, false]);
                if (ticked)
                {
                    api.Commit.Invoke(manager, ["Add Dependency"]);
                }

                return ticked;
            }
            finally
            {
                (presenter as IDisposable)?.Dispose();
            }
        }
        finally
        {
            (manager as IDisposable)?.Dispose();
        }
    }

    private static MethodInfo? addDependencyRoutine;

    /// <summary>
    /// The fallback: Service Studio's internal routine behind Search in other modules › Add dependency.
    /// Its name is obfuscated and may change between builds, so it's found by its shape: static,
    /// returning an enum, taking (IAggregatorPresenter, ESpace, IEnumerable&lt;ElementProperties&gt;).
    /// </summary>
    private static MethodInfo AddDependencyRoutine()
    {
        if (addDependencyRoutine != null)
        {
            return addDependencyRoutine;
        }

        var assembly = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter").Assembly;
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        var found = types
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType.IsEnum)
            .Where(m =>
            {
                var p = m.GetParameters();
                return p.Length == 3
                    && p[0].ParameterType.Name == "IAggregatorPresenter"
                    && p[1].ParameterType.Name == "ESpace"
                    && p[2].ParameterType.IsGenericType
                    && p[2].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    && p[2].ParameterType.GetGenericArguments()[0].Name == "ElementProperties";
            })
            .ToList();
        if (found.Count != 1)
        {
            throw new BridgeException($"This Service Studio build doesn't have the add-dependency routine the bridge knows ({found.Count} candidates). Add the dependency in Manage Dependencies.");
        }

        return addDependencyRoutine = found[0];
    }

    /// <summary>Sets a public field or property, on a class or a boxed struct.</summary>
    private static void SetMember(object target, string name, object? value, bool required = true)
    {
        var type = target.GetType();
        if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } field)
        {
            field.SetValue(target, value);
        }
        else if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } property)
        {
            property.SetValue(target, value);
        }
        else if (required)
        {
            throw new BridgeException($"Service Studio's {type.Name} has no {name} in this build.");
        }
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
