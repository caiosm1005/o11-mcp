using System.Collections;
using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// Creates, clones, moves and converts modules through Service Studio's own commands. createModule does
/// what the application page's Create Module dialog does: it names the module on the page, makes a
/// throwaway tab (MockAggregatorPresenter) for the window and runs one of the New…Module commands on
/// it. cloneModule runs what Module › Clone runs, then names the clone and sets the application it
/// publishes into. convertModule runs one of the Module › Convert commands on the module's tab. All
/// three leave an unsaved module in a Service Studio tab: nothing is saved or published. moveModule
/// moves a module into another application on the server, as the application page's Move does.
/// </summary>
internal static partial class Studio
{
    /// <summary>The kinds a module can be created with.</summary>
    private static readonly string[] ModuleKinds = ["Reactive", "Mobile", "Traditional", "Service", "ReactiveLibrary"];

    /// <summary>The kinds convertModule takes; Reactive only to say why it doesn't.</summary>
    private static readonly string[] ConversionTargets = ["Service", "ReactiveLibrary", "Reactive"];

    private sealed record CreateModuleApi(ConstructorInfo NewMock, Type NewModule, Type NewBlank, Type NewServiceBlank, Type NewLibrary,
        PropertyInfo DetailsInfo, PropertyInfo NewModuleName);

    private static CreateModuleApi? createModuleApi;

    /// <summary>The public Service Studio members createModule uses.</summary>
    private static CreateModuleApi CreateModuleMembers()
    {
        if (createModuleApi != null)
        {
            return createModuleApi;
        }

        var mock = Reflect.FindType("ServiceStudio.Presenter.Commands.MockAggregatorPresenter", "ServiceStudio.Presenter");
        var newMock = mock.GetConstructors().SingleOrDefault(c => c.GetParameters() is { Length: 6 } p
                && p[1].ParameterType.Name == "ServerCommunicationsProvider" && p[3].ParameterType.Name == "IAggregatorWindow")
            ?? throw new BridgeException("MockAggregatorPresenter(activeESpace, provider, register, window, topLevelPresenter, isFixed) is missing");
        Type Command(string name) => Reflect.FindType($"ServiceStudio.Presenter.Commands.ApplicationCommands+{name}", "ServiceStudio.Presenter");
        var page = Reflect.FindType("ServiceStudio.Presenter.IServerPagePresenter", "ServiceStudio.Presenter");
        var details = Reflect.FindType("ServiceStudio.Presenter.IApplicationDetailsPresenter", "ServiceStudio.Presenter").GetProperty("ApplicationInfo");
        var newModuleName = page.GetProperty("NewModuleName");
        if (details is not { CanWrite: true } || newModuleName is not { CanWrite: true }
            || page.GetMethod("ShowApplication") == null || page.GetProperty("ServerListPresenter") == null)
        {
            throw new BridgeException("a member of Service Studio's application page createModule uses is missing");
        }

        var api = new CreateModuleApi(newMock, Command("NewModule"), Command("NewBlankModule"), Command("NewServiceBlankModule"), Command("NewLibraryModule"),
            details, newModuleName);
        foreach (var command in new[] { api.NewModule, api.NewBlank, api.NewServiceBlank, api.NewLibrary })
        {
            _ = CommandInstance(command);
            _ = CommandMethod(command, "CanExecute");
            _ = CommandMethod(command, "Execute");
        }

        return createModuleApi = api;
    }

    /// <summary>A Service Studio command's single instance (AutoRegistryType&lt;T&gt;.Instance).</summary>
    private static object CommandInstance(Type command) =>
        command.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
        ?? throw new BridgeException($"{command.Name}.Instance is missing");

    /// <summary>A command's public AppliesTo, CanExecute or Execute taking (ICommandTarget, IPresenter).</summary>
    private static MethodInfo CommandMethod(Type command, string name) =>
        command.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name
            && m.GetParameters() is [{ ParameterType.Name: "ICommandTarget" }, { ParameterType.Name: "IPresenter" }])
        ?? throw new BridgeException($"{command.Name}.{name}(ICommandTarget, IPresenter) is missing");

    private static object? RunCommand(object instance, string name, object target) =>
        CommandMethod(instance.GetType(), name).Invoke(instance, [target, target]);

    public static object CreateModule(string? host, string application, string name, string? kind, bool template)
    {
        var wantedKind = kind == null ? null : KindArgument(kind, "kind", ModuleKinds);
        var connection = Pick(host);
        var api = CreateModuleMembers();
        CheckNewModuleName(connection, name, Modules(connection));
        var found = FindApplication(connection, application);
        var appName = Reflect.TryGet(found, "Name") as string ?? application;
        var key = Reflect.Get(found, "Key");
        var serverPage = ServerPage(connection);
        var info = ApplicationRecord(connection, serverPage, found);
        var appKind = KindName(Reflect.TryGet(info, "ApplicationKind"));
        var moduleKind = wantedKind ?? appKind;

        // As the Create Module dialog picks: the application's own kind (from its template, or blank),
        // a blank Service module in any application, a Library in a Reactive, Phone or Library one.
        Type command;
        if (moduleKind == appKind && appKind is "Reactive" or "Mobile" or "Traditional" or "Service")
        {
            command = template ? api.NewModule : api.NewBlank;
        }
        else if (moduleKind == "Service")
        {
            command = api.NewServiceBlank;
        }
        else if (moduleKind == "ReactiveLibrary" && appKind is "Reactive" or "Mobile" or "ReactiveLibrary")
        {
            command = api.NewLibrary;
        }
        else
        {
            throw new BridgeException($"{appName} is a {appKind} application: it takes {KindList(AllowedModuleKinds(appKind))} modules, not {moduleKind}. "
                + $"A {moduleKind} module needs a {moduleKind} application (createApplication makes one).");
        }

        if (moduleKind == "Mobile" && Reflect.TryGet(Reflect.TryGet(connection.Provider, "ServerLicensingInfo"), "MobileAppLicense")?.ToString() != "Unlimited")
        {
            throw new BridgeException("This environment's license doesn't allow creating Phone modules freely: Service Studio asks about it first. Create it in Service Studio.");
        }

        // The page has to show the application: the commands read it from the page's details.
        InContext(serverPage, $"Showing the application {appName}", () => Reflect.Call(serverPage, "ShowApplication", key, appName), wait: true);
        var detailsType = api.DetailsInfo.DeclaringType!;
        object? details = null;
        for (var i = 0; i < 40 && details == null; i++)
        {
            details = Reflect.TryGet(serverPage, "ServerListPresenter") is { } list && detailsType.IsInstanceOfType(list) ? list : null;
            if (details == null)
            {
                Thread.Sleep(250);
            }
        }

        if (details == null)
        {
            throw new BridgeException($"Service Studio didn't show {appName}'s page, which the new module's command reads.");
        }

        var window = Reflect.Call(connection.Presenter, "GetOwnerAggregatorWindow");
        string? refused = null;
        InContext(serverPage, $"Creating the module {name} in {appName}", () =>
        {
            api.DetailsInfo.SetValue(details, info);
            var previousName = api.NewModuleName.GetValue(serverPage);
            api.NewModuleName.SetValue(serverPage, name);
            var mock = api.NewMock.Invoke([null, connection.Provider, false, window, serverPage, true]);
            try
            {
                var instance = CommandInstance(command);
                if (RunCommand(instance, "CanExecute", mock) is not true)
                {
                    refused = $"Service Studio doesn't allow a {moduleKind} module in {appName} here (a system application, or a feature this environment lacks).";
                    return;
                }

                RunCommand(instance, "Execute", mock);
            }
            finally
            {
                (mock as IDisposable)?.Dispose();
                api.NewModuleName.SetValue(serverPage, previousName);
            }
        }, wait: true);
        if (refused != null)
        {
            throw new BridgeException(refused);
        }

        var tab = WaitForTab(connection, name, null);
        var created = Reflect.TryGet(tab, "OwnerESpace");
        return Scoped(connection, new
        {
            Module = name,
            Kind = KindName(Reflect.TryGet(created, "ESpaceType")),
            Application = appName,
            Status = "created",
            Note = (template && command != api.NewModule ? $"template applies only to the application's own kind; this {moduleKind} module is blank. " : string.Empty)
                + "Open in a new Service Studio tab, unsaved: it reaches the server once published (omlPublish, when the user asks). "
                + "Until then it's only in Service Studio, and closing its tab without saving loses it.",
        });
    }

    /// <summary>The module kinds an application of this kind takes, as Service Studio's Create Module dialog offers them.</summary>
    private static string[] AllowedModuleKinds(string appKind) => appKind switch
    {
        "Reactive" or "Mobile" => [appKind, "Service", "ReactiveLibrary"],
        "ReactiveLibrary" => ["ReactiveLibrary", "Service"],
        "Traditional" => ["Traditional", "Service"],
        _ => ["Service"],
    };

    private static string KindList(string[] kinds) =>
        kinds.Length == 1 ? kinds[0] : $"{string.Join(", ", kinds[..^1])} and {kinds[^1]}";

    /// <summary>
    /// Refuses a name Service Studio would change (StringUtils.FixName), or one a module in the
    /// environment, or a module open in Service Studio, has already.
    /// </summary>
    private static void CheckNewModuleName(Connection connection, string name, List<object> modules)
    {
        var fixName = Reflect.FindType("OutSystems.RuntimeCommon.StringUtils", "OutSystems.RuntimeCommon")
            .GetMethod("FixName", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(bool)]);
        if (fixName?.Invoke(null, [name, false]) is string fixedName && fixedName != name)
        {
            throw new BridgeException($"{name} isn't a valid module name; Service Studio would make it {fixedName}.");
        }

        var taken = modules.FirstOrDefault(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
        if (taken != null)
        {
            throw new BridgeException($"A module named {name} exists already, in {Reflect.TryGet(taken, "ApplicationName")}: module names are unique in the environment.");
        }

        if (OpenTab(connection, name) != null)
        {
            throw new BridgeException($"A module named {name} is open in Service Studio already, not published yet: pick another name, or close that one.");
        }
    }

    private sealed record CloneModuleApi(MethodInfo CloneESpace, Type Tab, MethodInfo SetName, MethodInfo Execute, Type Runtime, PropertyInfo AutoClose);

    /// <summary>A tab's publish details (IPublishInfoProvider): the application its module publishes into, by name and key.</summary>
    private sealed record PublishTargetApi(PropertyInfo PublishInfo, PropertyInfo OwnerApplicationName, PropertyInfo OwnerApplicationKey);

    private static PublishTargetApi? publishTargetApi;

    /// <summary>
    /// Where Service Studio records the application a module open in a tab publishes into: Create
    /// Module sets it for a new module, and the application page's Move updates it for an open one.
    /// </summary>
    private static PublishTargetApi PublishTargetMembers()
    {
        if (publishTargetApi != null)
        {
            return publishTargetApi;
        }

        var tab = Reflect.FindType("ServiceStudio.Presenter.AggregatorPresenter", "ServiceStudio.Presenter");
        var publishInfo = tab.GetInterfaces().Select(i => i.GetProperty("PublishInfoProvider")).FirstOrDefault(p => p != null)
            ?? throw new BridgeException("A tab's PublishInfoProvider is missing");
        PropertyInfo Writable(string name) => publishInfo.PropertyType.GetProperty(name) is { CanWrite: true } p
            ? p
            : throw new BridgeException($"IPublishInfoProvider.{name} is missing or read-only");
        return publishTargetApi = new PublishTargetApi(publishInfo, Writable("OwnerApplicationName"), Writable("OwnerApplicationKey"));
    }

    /// <summary>The application a module open in a tab publishes into, by name (empty when none is set).</summary>
    private static string? PublishApplication(object tab)
    {
        var api = PublishTargetMembers();
        return api.OwnerApplicationName.GetValue(api.PublishInfo.GetValue(tab)) as string;
    }

    /// <summary>Sets the application a module open in a tab publishes into (call in the tab's context).</summary>
    private static void SetPublishApplication(object tab, string name, object? key)
    {
        var api = PublishTargetMembers();
        var publishInfo = api.PublishInfo.GetValue(tab);
        api.OwnerApplicationKey.SetValue(publishInfo, key);
        api.OwnerApplicationName.SetValue(publishInfo, name);
    }

    /// <summary>
    /// The server's record of a module argument: for an open module, the one with its key (none
    /// when it was never published); else the one with the key or name given.
    /// </summary>
    private static object? ServerModule(List<object> modules, object? tab, string module, string name)
    {
        if (tab != null)
        {
            var openKey = ModuleGuid(Reflect.TryGet(tab, "OwnerESpace"));
            return modules.FirstOrDefault(m => KeyGuid(Reflect.TryGet(m, "Key")) == openKey);
        }

        return (Guid.TryParse(module.Trim(), out var key) ? modules.FirstOrDefault(m => KeyGuid(Reflect.TryGet(m, "Key")) == key) : null)
            ?? modules.FirstOrDefault(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
    }

    private static CloneModuleApi? cloneModuleApi;

    /// <summary>
    /// The public Service Studio members cloneModule uses: what Module › Clone runs
    /// (IAggregatorPresenter.CloneESpace, here without its file dialog), ESpace.SetName in an undoable
    /// step (Command.Execute) for the name, the one-shot switch that closes the "Module cloned"
    /// message (Runtime.ShouldAutoCloseNextDialog), and the tab's publish details (PublishTargetMembers).
    /// </summary>
    private static CloneModuleApi CloneModuleMembers()
    {
        if (cloneModuleApi != null)
        {
            return cloneModuleApi;
        }

        var eSpace = Reflect.FindType("ServiceStudio.Model.ESpace", "ServiceStudio.Framework");
        var aggregator = Reflect.FindType("ServiceStudio.Presenter.IAggregatorPresenter", "ServiceStudio.Presenter");
        var clone = aggregator.GetMethods().SingleOrDefault(m => m.Name == "CloneESpace" && m.GetParameters() is { Length: 4 } p
                && p[0].ParameterType == eSpace && p[1].ParameterType == typeof(bool) && p[2].ParameterType == typeof(bool) && p[3].ParameterType == eSpace.MakeByRefType())
            ?? throw new BridgeException("IAggregatorPresenter.CloneESpace(ESpace, bool, bool, out ESpace) is missing");
        var setName = eSpace.GetMethod("SetName", BindingFlags.Public | BindingFlags.Instance, [typeof(string)])
            ?? throw new BridgeException("ESpace.SetName(string) is missing");
        var execute = Reflect.FindType("ServiceStudio.Commands.Command", "ServiceStudio.Framework").GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(m => m.Name == "Execute" && m.GetParameters() is { Length: 3 } p && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(System.Action))
            ?? throw new BridgeException("Command.Execute(context, string, Action) is missing");
        var runtimeType = Reflect.FindType("ServiceStudio.Runtime", "ServiceStudio.Framework");
        _ = runtimeType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) ?? throw new BridgeException("Runtime.Instance is missing");
        var autoClose = runtimeType.GetProperty("ShouldAutoCloseNextDialog") is { CanRead: true, CanWrite: true } property
            ? property
            : throw new BridgeException("Runtime.ShouldAutoCloseNextDialog is missing");

        // The switch answers a message with Close when it has that button or none, else Yes, OK or
        // Cancel: "Module cloned" has to be one it closes.
        foreach (var message in new[] { "Clone", "CloneToIndependentModules" })
        {
            var buttons = Reflect.Enumerate(Reflect.TryGet(MessageDefinitionOf(PresenterMessage(message)), "Buttons")).Select(b => Reflect.TryGet(b, "Type")?.ToString()).ToList();
            if (buttons.Count > 0 && !buttons.Contains("Close") && !buttons.Contains("Ok"))
            {
                throw new BridgeException("Service Studio's \"Module cloned\" message has buttons the bridge can't close in this build");
            }
        }

        _ = PublishTargetMembers();
        return cloneModuleApi = new CloneModuleApi(clone, Reflect.FindType("ServiceStudio.Presenter.AggregatorPresenter", "ServiceStudio.Presenter"),
            setName, execute, runtimeType, autoClose);
    }

    /// <summary>
    /// Clones a module as Module › Clone does: a copy with a new module key, opened in a new tab,
    /// unsaved. The source is the module open in Service Studio, as the tab has it, or else its
    /// published version, loaded in memory without a tab. The clone gets the name given, through an
    /// undoable rename as its properties do, and the application it publishes into, as Create Module
    /// sets it. Nothing is saved or published.
    /// </summary>
    public static object CloneModule(string? host, string module, string name, string? application)
    {
        var (connection, tab, sourceName) = ResolveOpenModule(host, module);
        var api = CloneModuleMembers();
        var modules = Modules(connection);
        CheckNewModuleName(connection, name, modules);

        var published = ServerModule(modules, tab, module, sourceName);
        if (tab == null && published == null)
        {
            if (module.Contains('/'))
            {
                throw NotOpen(module, connection);
            }

            _ = FindModule(connection, module, modules);
        }

        module = tab != null ? sourceName : Reflect.TryGet(published, "Name") as string ?? sourceName;

        if (tab == null && Reflect.TryGet(published, "Kind")?.ToString() == "Extension")
        {
            throw new BridgeException($"{module} is an extension: Service Studio clones modules only.");
        }

        // The application: the one asked for, else the one the source publishes into.
        var appName = application;
        if (appName == null && tab != null && PublishApplication(tab) is { Length: > 0 } owner)
        {
            appName = owner;
        }

        appName ??= Reflect.TryGet(published, "ApplicationName") as string
            ?? throw new BridgeException($"{module} isn't in an application on the server: pass application, the one the clone publishes into.");
        var app = FindApplication(connection, appName);
        appName = Reflect.TryGet(app, "Name") as string ?? appName;
        var appKey = Reflect.Get(app, "Key");
        var appKind = KindName(Reflect.TryGet(ApplicationRecord(connection, ServerPage(connection), app), "ApplicationKind"));

        // Module › Clone runs on the module's tab; a module loaded in memory has none, so any tab of
        // the window runs it, and Service Studio opens the clone in a new tab either way. Checked
        // before the download, which can take a while.
        var runner = tab ?? OpenTabs(connection).FirstOrDefault(api.Tab.IsInstanceOfType)
            ?? throw new BridgeException($"Service Studio clones from a module's tab, and none is open on {connection.Host}: open {module} (openModule) "
                + "or any other module, then try again.");

        object? loaded = null;
        object source;
        string readFrom;
        string? warning = null;
        if (tab != null)
        {
            source = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");
            readFrom = "open in Service Studio, as its tab has it (unsaved changes included)";
            switch (EditSessionOn(source, out var sessionProblem))
            {
                case true:
                    warning = $"Service Studio's MCP server has an edit session (or unmerged changes) on {module}: what it hasn't merged isn't in the clone.";
                    break;
                case null:
                    warning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}): changes one holds unmerged aren't in the clone.";
                    break;
            }
        }
        else
        {
            var key = Reflect.Get(published!, "Key") ?? throw new BridgeException($"The server gave no key for {module}.");
            var (_, bytes) = Run(() => Download(connection, 0, key), timeoutSeconds: 300);
            loaded = Run(() => ElementConsumersMembers().Load.Invoke(null, [bytes, null, null, null, null, null, null]))
                ?? throw new BridgeException($"Service Studio loaded nothing from {module}'s OML.");
            source = loaded;
            readFrom = $"its published version (version {Reflect.TryGet(published, "Version")}), loaded in memory";
        }

        var kind = KindName(Reflect.TryGet(source, "ESpaceType"));
        if (!AllowedModuleKinds(appKind).Contains(kind))
        {
            (loaded as IDisposable)?.Dispose();
            throw new BridgeException($"{appName} is a {appKind} application: it takes {KindList(AllowedModuleKinds(appKind))} modules, not {kind}. "
                + "Pass application: one the clone fits.");
        }

        var runtime = Reflect.Static(api.Runtime, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        object? clone = null;
        var unanswered = false;
        try
        {
            InContext(runner, $"Cloning {module}", () =>
            {
                api.AutoClose.SetValue(runtime, true);
                try
                {
                    var args = new object?[] { source, false, false, null };
                    clone = api.CloneESpace.Invoke(runner, args) ?? args[3];
                }
                finally
                {
                    // Reading it clears it: true here means no message took it.
                    unanswered = api.AutoClose.GetValue(runtime) is true;
                }
            }, wait: true);
        }
        finally
        {
            (loaded as IDisposable)?.Dispose();
        }

        if (clone == null)
        {
            throw new BridgeException($"Service Studio didn't clone {module}: look in Service Studio for a message.");
        }

        object? cloneTab = null;
        for (var i = 0; i < 120 && cloneTab == null; i++)
        {
            cloneTab = OpenTabs(connection).FirstOrDefault(a => ReferenceEquals(Reflect.TryGet(a, "OwnerESpace"), clone));
            if (cloneTab == null)
            {
                Thread.Sleep(250);
            }
        }

        var defaultName = Reflect.TryGet(clone, "Name") as string;
        if (cloneTab == null)
        {
            throw new BridgeException($"Service Studio cloned {module} as {defaultName}, but its tab didn't show within 30 s: look in Service Studio. "
                + $"Rename it {name} in its properties.");
        }

        string? named = null;
        InContext(cloneTab, $"Naming the clone of {module} {name}", () =>
        {
            System.Action rename = () => api.SetName.Invoke(clone, [name]);
            api.Execute.Invoke(null, [Reflect.Call(cloneTab, "GetPresenterContext"), "Name", rename]);
            SetPublishApplication(cloneTab, appName, appKey);
            named = Reflect.TryGet(clone, "Name") as string;
        }, wait: true);
        if (!string.Equals(named, name, StringComparison.Ordinal))
        {
            warning = (warning == null ? string.Empty : warning + " ") + $"Service Studio named the clone {named}, not {name}: rename it in its properties.";
        }

        return Scoped(connection, new
        {
            Module = named,
            From = module,
            ReadFrom = readFrom,
            Kind = kind,
            Application = appName,
            Status = "cloned",
            Note = "Open in a new Service Studio tab, unsaved, with a new module key: nothing reached the server. Publishing it (omlPublish, when the user asks) "
                + $"creates it in {appName} as a new module, with its own tables for its entities. Until then it's only in Service Studio: closing its tab "
                + "without saving loses it, and a copy saved to a file and reopened no longer knows its application."
                + (unanswered ? string.Empty : " The bridge closed Service Studio's \"Module cloned\" message."),
            Warning = warning,
        });
    }

    private sealed record MoveModuleApi(MethodInfo Move, Type ModuleKind, MethodInfo? IsSystemApplication);

    private static MoveModuleApi? moveModuleApi;

    /// <summary>
    /// The public Service Studio members moveModule uses: the server call the application page's
    /// Move runs (ServerCommunicationsProvider.MoveModuleBetweenApps), the platform's check for a
    /// system application (Constants.IsSystemApplication, when this build has it), and the tab's
    /// publish details, which Move updates for a module open in a tab.
    /// </summary>
    private static MoveModuleApi MoveModuleMembers()
    {
        if (moveModuleApi != null)
        {
            return moveModuleApi;
        }

        var provider = Reflect.FindType("ServiceStudio.ServerCommunications.ServerCommunicationsProvider", "ServiceStudio.ServerCommunications");
        var move = provider.GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(m => m.Name == "MoveModuleBetweenApps"
                && m.GetParameters() is { Length: 5 } p && p[2].ParameterType.IsEnum && p[4].ParameterType.IsByRef)
            ?? throw new BridgeException("ServerCommunicationsProvider.MoveModuleBetweenApps(from, to, kind, module, out message) is missing");
        var moduleKind = move.GetParameters()[2].ParameterType;
        if (!Enum.GetNames(moduleKind).Contains("Espace") || !Enum.GetNames(moduleKind).Contains("Extension"))
        {
            throw new BridgeException($"{moduleKind.Name} has no Espace or Extension in this build");
        }

        _ = PublishTargetMembers();
        _ = ApplicationMembers();
        var isSystem = Reflect.FindType("OutSystems.RuntimeCommon.Constants", "OutSystems.RuntimeCommon").GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "IsSystemApplication" && m.GetParameters().Length == 1 && m.ReturnType == typeof(bool));
        return moveModuleApi = new MoveModuleApi(move, moduleKind, isSystem);
    }

    /// <summary>
    /// Moves a module into another application, as the application page's Move does: on the server
    /// at once, the module itself unchanged. A module open but never published isn't on the server:
    /// for it, as for Service Studio's pending modules, only the application it publishes into
    /// changes. Service Studio's own limits apply: no system application on either side, not into
    /// Independent Modules, a target that takes the module's kind, and not an application's last
    /// module, whose move deletes the application once Service Studio has asked.
    /// </summary>
    public static object MoveModule(string? host, string module, string application)
    {
        var (connection, tab, name) = ResolveOpenModule(host, module);
        var api = MoveModuleMembers();
        var modules = Modules(connection);
        var found = ServerModule(modules, tab, module, name);
        if (tab == null && found == null)
        {
            if (module.Contains('/'))
            {
                throw NotOpen(module, connection);
            }

            found = FindModule(connection, module, modules);
        }

        module = Reflect.TryGet(found, "Name") as string ?? name;
        var target = FindApplication(connection, application);
        var targetName = Reflect.TryGet(target, "Name") as string ?? application;
        var targetKey = Reflect.Get(target, "Key") ?? throw new BridgeException($"The server gave no key for {targetName}.");
        var others = ApplicationMembers().OthersApplication;
        bool IsSystem(object? key) => key != null && api.IsSystemApplication?.Invoke(null, [key]) is true;
        if (KeyGuid(targetKey) == KeyGuid(others))
        {
            throw new BridgeException("Service Studio moves modules into applications, not into Independent Modules.");
        }

        if (IsSystem(targetKey))
        {
            throw new BridgeException($"{targetName} is a system application: Service Studio doesn't move modules into one.");
        }

        // As Service Studio's Move lists targets: an extension or a Service module goes in any
        // application, a Library in a Reactive, Phone or Library one, any other module in one of its kind.
        var isExtension = Reflect.TryGet(found, "Kind")?.ToString() == "Extension";
        var kind = isExtension ? "Extension" : KindName(found != null ? Reflect.TryGet(found, "ESpaceKind") : Reflect.TryGet(Reflect.TryGet(tab, "OwnerESpace"), "ESpaceType"));
        var targetKind = KindName(Reflect.TryGet(ApplicationRecord(connection, ServerPage(connection), target), "ApplicationKind"));
        var fits = isExtension || kind == "Service" || (kind.EndsWith("Library", StringComparison.Ordinal)
            ? targetKind is "Reactive" or "Mobile" or "ReactiveLibrary"
            : kind == targetKind);
        if (!fits)
        {
            throw new BridgeException($"{targetName} is a {targetKind} application, and Service Studio doesn't move a {kind} module into it: a {kind} module goes in "
                + (kind.EndsWith("Library", StringComparison.Ordinal) ? "a Reactive, Phone or Library application." : $"a {kind} application."));
        }

        if (found == null)
        {
            // Never published: as for Service Studio's pending modules, only where it publishes changes.
            var before = PublishApplication(tab!);
            if (string.Equals(before, targetName, StringComparison.OrdinalIgnoreCase))
            {
                return Scoped(connection, new { Module = module, Application = targetName, Status = "unchanged", Note = $"It publishes into {targetName} already." });
            }

            InContext(tab!, $"Setting the application {module} publishes into", () => SetPublishApplication(tab!, targetName, targetKey), wait: true);
            return Scoped(connection, new
            {
                Module = module,
                Before = string.IsNullOrEmpty(before) ? null : before,
                After = PublishApplication(tab!),
                Status = "retargeted",
                Note = $"{module} isn't on the server yet (never published), so nothing changed there: its first publish (omlPublish, when the user asks) creates it in "
                    + $"{targetName}. Until then that's only in its Service Studio tab: a copy saved to a file and reopened no longer knows it.",
            });
        }

        var fromName = Reflect.TryGet(found, "ApplicationName") as string;
        var fromKey = Reflect.TryGet(found, "ApplicationKey") ?? others;
        var moduleKey = Reflect.Get(found, "Key") ?? throw new BridgeException($"The server gave no key for {module}.");
        if (KeyGuid(fromKey) == KeyGuid(targetKey))
        {
            return Scoped(connection, new { Module = module, Application = targetName, Status = "unchanged", Note = $"{module} is in {targetName} already." });
        }

        if (IsSystem(fromKey))
        {
            throw new BridgeException($"{module} is in {fromName}, a system application: Service Studio doesn't move modules out of one.");
        }

        if (KeyGuid(fromKey) != KeyGuid(others)
            && !modules.Any(m => !ReferenceEquals(m, found) && KeyGuid(Reflect.TryGet(m, "ApplicationKey")) == KeyGuid(fromKey)))
        {
            throw new BridgeException($"{module} is the last module of {fromName}: moving it deletes {fromName}, which Service Studio asks about first. "
                + "If the user wants that, they move it in Service Studio (the module's menu on the application's page).");
        }

        var args = new object?[] { fromKey, targetKey, Enum.Parse(api.ModuleKind, isExtension ? "Extension" : "Espace"), moduleKey, null };
        if (Run(() => api.Move.Invoke(connection.Provider, args)) is not true)
        {
            throw new BridgeException($"The server didn't move {module} into {targetName}: " + (args[4] == null ? "it gave no reason." : MessageText(args[4])));
        }

        // As Move does: the tabs holding the module publish it into its new application.
        foreach (var open in OpenTabs(connection).Where(t => ModuleGuid(Reflect.TryGet(t, "OwnerESpace")) == KeyGuid(moduleKey)).ToList())
        {
            InContext(open, $"Setting the application {module} publishes into", () => SetPublishApplication(open, targetName, targetKey), wait: true);
        }

        var after = Modules(connection).FirstOrDefault(m => KeyGuid(Reflect.TryGet(m, "Key")) == KeyGuid(moduleKey));
        var afterName = Reflect.TryGet(after, "ApplicationName") as string;
        return Scoped(connection, new
        {
            Module = module,
            Before = fromName,
            After = afterName,
            Status = "moved",
            Note = "Moved on the server, as the application page's Move does. The module itself didn't change: nothing was published, and its versions stay as they are.",
            Warning = string.Equals(afterName, targetName, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"The server took the move, but now lists {module} in {afterName ?? "no application"}: check it in Service Studio.",
        });
    }

    /// <summary>
    /// The tab of a module (by name) once it's there and, when kind is given, of that kind; fails after
    /// half a minute.
    /// </summary>
    private static object WaitForTab(Connection connection, string module, string? kind)
    {
        for (var i = 0; i < 120; i++)
        {
            if (OpenTab(connection, module) is { } tab
                && (kind == null || KindName(Reflect.TryGet(Reflect.TryGet(tab, "OwnerESpace"), "ESpaceType")) == kind))
            {
                return tab;
            }

            Thread.Sleep(250);
        }

        throw new BridgeException($"Service Studio didn't open {module}{(kind == null ? string.Empty : $" as {kind}")} within 30 s: look in Service Studio for a message.");
    }

    private sealed record ConvertModuleApi(List<(object Command, string Target)> Commands, Type Runtime, PropertyInfo AutoClose, HashSet<string> Questions);

    private static ConvertModuleApi? convertModuleApi;

    /// <summary>
    /// The public Service Studio members convertModule uses: the Module › Convert commands (every
    /// AbstractConvertModuleCommand but Phone→Reactive), with the kind each converts to; the one-shot
    /// switch that answers the next message (Runtime.ShouldAutoCloseNextDialog); and the ids of the
    /// "publish now?" questions that follow, whose Cancel answer must be No.
    /// </summary>
    private static ConvertModuleApi ConvertModuleMembers()
    {
        if (convertModuleApi != null)
        {
            return convertModuleApi;
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

        static bool IsConversion(Type? type)
        {
            for (; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "ServiceStudio.Presenter.Commands.AbstractConvertModuleCommand`1")
                {
                    return true;
                }
            }

            return false;
        }

        var commands = types
            .Where(t => !t.IsAbstract && (t.IsPublic || t.IsNestedPublic) && t.Name != "ConvertToReactive" && IsConversion(t))
            .Select(t => (Command: CommandInstance(t), Type: t))
            .Select(c => (c.Command, Target: KindName(PrivateField(c.Command, "TargetModuleKind"))))
            .ToList();
        if (commands.Count == 0)
        {
            throw new BridgeException("Service Studio's Module › Convert commands are missing");
        }

        foreach (var (command, _) in commands)
        {
            _ = CommandMethod(command.GetType(), "AppliesTo");
            _ = CommandMethod(command.GetType(), "CanExecute");
            _ = CommandMethod(command.GetType(), "Execute");
            _ = command.GetType().GetMethod("GetElementsNotAllowedForModuleKind") ?? throw new BridgeException("GetElementsNotAllowedForModuleKind is missing");
        }

        var runtimeType = Reflect.FindType("ServiceStudio.Runtime", "ServiceStudio.Framework");
        _ = runtimeType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) ?? throw new BridgeException("Runtime.Instance is missing");
        var autoClose = runtimeType.GetProperty("ShouldAutoCloseNextDialog") is { CanRead: true, CanWrite: true } property
            ? property
            : throw new BridgeException("Runtime.ShouldAutoCloseNextDialog is missing");
        // The switch answers a message with Close if it has that button, else Yes: the confirmation
        // must offer Yes and no Close, or the conversion would be declined.
        var confirm = Reflect.Enumerate(Reflect.TryGet(MessageDefinitionOf(PresenterMessage("ConvertModule_Valid")), "Buttons"))
            .Select(b => Reflect.TryGet(b, "Type")?.ToString())
            .ToList();
        if (!confirm.Contains("Yes") || confirm.Contains("Close"))
        {
            throw new BridgeException("Service Studio's conversion confirmation has buttons the bridge can't answer with Yes in this build");
        }

        var question = PresenterMessage("ConvertModule_Converted");
        if (Reflect.TryGet(MessageDefinitionOf(question), "CancelButton")?.ToString() != "No")
        {
            throw new BridgeException("Service Studio's \"publish now?\" question after a conversion doesn't answer No when closed in this build");
        }

        var questions = new HashSet<string>(MessageIdTexts(question).Concat(MessageIdTexts(PresenterMessage("ConvertModule_Converted_WithErrors"))), StringComparer.Ordinal);
        return convertModuleApi = new ConvertModuleApi(commands, runtimeType, autoClose, questions);
    }

    public static object ConvertModule(string? host, string module, string to)
    {
        var target = KindArgument(to, "to", ConversionTargets);
        if (target == "Reactive")
        {
            throw new BridgeException("convertModule doesn't convert to Reactive: Service Studio's Phone→Reactive conversion also changes the application's kind on the "
                + "server and publishes the module, so it's left to the user (Module › Convert › Convert to Reactive in Service Studio). Traditional→Reactive "
                + "isn't a Service Studio conversion: it's OutSystems' separate migration tool.");
        }

        var (connection, tab, name) = ResolveOpenModule(host, module);
        if (tab == null)
        {
            throw NotOpen(module, connection);
        }

        module = name;
        var eSpace = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");
        var from = KindName(Reflect.TryGet(eSpace, "ESpaceType"));
        if (from == target)
        {
            throw new BridgeException($"{module} is a {target} module already.");
        }

        string? warning = null;
        switch (EditSessionOn(eSpace, out var sessionProblem))
        {
            case true:
                throw new BridgeException($"Service Studio's MCP server has an edit session (or unmerged changes) on {module}. Converting closes the module's tab "
                    + "and reopens it converted, so merge what the session holds (omlMerge), end it (endOmlEditSession), then convert.");
            case null:
                warning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}). If one was open, end it: the module's tab was replaced.";
                break;
        }

        var api = ConvertModuleMembers();
        (object Command, string Target)? chosen = null;
        var available = new List<string>();
        string? refusal = null;
        InContext(tab, $"Checking {module}'s conversions", () =>
        {
            foreach (var conversion in api.Commands)
            {
                if (RunCommand(conversion.Command, "AppliesTo", tab) is true && RunCommand(conversion.Command, "CanExecute", tab) is true)
                {
                    available.Add(conversion.Target);
                    if (chosen == null && conversion.Target == target)
                    {
                        chosen = conversion;
                    }
                }
            }

            if (chosen == null)
            {
                return;
            }

            if (Reflect.TryGet(eSpace, "IsValid") is not true)
            {
                refusal = $"{module} has errors, and Service Studio converts only a valid module: fix them first (getValidationMessages lists them).";
                return;
            }

            // The elements the target kind can't hold, as Service Studio reports them, with their count.
            if (Reflect.Call(chosen.Value.Command, "GetElementsNotAllowedForModuleKind", eSpace) is IDictionary { Count: > 0 } forbidden)
            {
                // A generic Dictionary enumerates KeyValuePairs: read it through IDictionary's keys and indexer.
                var lines = forbidden.Keys.Cast<object>()
                    .Select(key => Reflect.TryCall(key, "GetReportText", forbidden[key]) as string ?? $"{forbidden[key]} {key}");
                refusal = $"{module} has elements a {target} module can't hold: {string.Join("; ", lines)}. Remove or move them, then convert.";
            }
        }, wait: true);
        if (chosen == null)
        {
            throw new BridgeException($"Service Studio doesn't convert this {from} module to {target}."
                + (available.Count > 0 ? $" It converts it to: {string.Join(", ", available.Distinct())}." : " It offers no conversion for it."));
        }

        if (refusal != null)
        {
            throw new BridgeException(refusal);
        }

        // The command asks to confirm, then closes the tab and reopens the module converted, whose tab
        // asks whether to publish now: the switch answers the confirmation with Yes, the watch the
        // publish question with No (its Cancel answer).
        var runtime = Reflect.Static(api.Runtime, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        WatchConversion(module, connection.Host, api.Questions);
        // Service Studio reopens the converted module from a file, which runs its reference check
        // again: handle that as after openModule (cancel it, close its messages).
        WatchForMissingDependencies(module, connection.Host, Reflect.TryGet(eSpace, "Key")?.ToString());
        var unanswered = false;
        InContext(tab, $"Converting {module} to {target}", () =>
        {
            api.AutoClose.SetValue(runtime, true);
            try
            {
                RunCommand(chosen.Value.Command, "Execute", tab);
            }
            finally
            {
                // Reading it clears it: true here means no message took it.
                unanswered = api.AutoClose.GetValue(runtime) is true;
            }
        }, wait: true);
        if (unanswered)
        {
            throw new BridgeException($"Service Studio didn't ask to confirm converting {module}, so it didn't convert it: look in Service Studio for a message.");
        }

        _ = WaitForTab(connection, module, target);
        return Scoped(connection, new
        {
            Module = module,
            From = from,
            To = target,
            Status = "converted",
            Note = "Unsaved, in a new Service Studio tab (Service Studio closed the old one). The bridge answers Service Studio's \"publish now?\" with No; "
                + "status lists it under AnsweredPrompts. Publish with omlPublish when the user asks, then refresh its consumers' references.",
            Warning = warning,
        });
    }
}
