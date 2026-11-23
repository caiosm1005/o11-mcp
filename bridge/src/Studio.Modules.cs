using System.Collections;
using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// Creates modules and converts their kind through Service Studio's own commands. createModule does
/// what the application page's Create Module dialog does: it names the module on the page, makes a
/// throwaway tab (MockAggregatorPresenter) for the window and runs one of the New…Module commands on
/// it. convertModule runs one of the Module › Convert commands on the module's tab. Both leave an
/// unsaved module in a Service Studio tab: nothing is saved or published.
/// </summary>
internal static partial class Studio
{
    /// <summary>The kinds a module can be created with.</summary>
    private static readonly string[] ModuleKinds = ["Reactive", "Mobile", "Traditional", "Service", "ReactiveLibrary"];

    /// <summary>The kinds convertModule takes; Reactive only to say why it doesn't.</summary>
    private static readonly string[] ConversionTargets = ["Service", "ReactiveLibrary", "Reactive"];

    private sealed record CreateModuleApi(ConstructorInfo NewMock, Type NewModule, Type NewBlank, Type NewServiceBlank, Type NewLibrary,
        MethodInfo? FixName, PropertyInfo DetailsInfo, PropertyInfo NewModuleName);

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

        var fixName = Reflect.FindType("OutSystems.RuntimeCommon.StringUtils", "OutSystems.RuntimeCommon")
            .GetMethod("FixName", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(bool)]);
        var api = new CreateModuleApi(newMock, Command("NewModule"), Command("NewBlankModule"), Command("NewServiceBlankModule"), Command("NewLibraryModule"),
            fixName, details, newModuleName);
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
        if (api.FixName?.Invoke(null, [name, false]) is string fixedName && fixedName != name)
        {
            throw new BridgeException($"{name} isn't a valid module name; Service Studio would make it {fixedName}.");
        }

        var taken = Modules(connection).FirstOrDefault(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
        if (taken != null)
        {
            throw new BridgeException($"A module named {name} exists already, in {Reflect.TryGet(taken, "ApplicationName")}: module names are unique in the environment.");
        }

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
            var allowed = appKind switch
            {
                "Reactive" or "Mobile" => $"{appKind}, Service and ReactiveLibrary",
                "ReactiveLibrary" => "ReactiveLibrary and Service",
                "Traditional" => "Traditional and Service",
                _ => "Service",
            };
            throw new BridgeException($"{appName} is a {appKind} application: it takes {allowed} modules, not {moduleKind}. "
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

        var connection = Pick(host);
        var tab = OpenTab(connection, module)
            ?? throw new BridgeException($"{module} isn't open in Service Studio (signed in to {connection.Host}). Open it first.");
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
