using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;

namespace ServiceStudioBridge;

/// <summary>
/// Service Studio's dialogs, seen through Avalonia, the UI framework it runs on. A modal dialog
/// stops a module tab's queue, where the bridge's commands wait, but Avalonia's UI dispatcher keeps
/// running posted work while one is open: so the bridge can tell which dialog is in the way, and
/// handle the reference check that follows openModule itself.
/// </summary>
internal static partial class Studio
{
    /// <summary>How long after openModule the bridge watches for the reference check and its prompt.</summary>
    private static readonly TimeSpan PromptWatch = TimeSpan.FromMinutes(3);

    /// <summary>A module openModule is opening: when it was asked, the environment's host, and the module's key.</summary>
    private sealed record PendingOpen(DateTime Since, string? Host, string? Key);

    /// <summary>The modules openModule is opening, by name.</summary>
    private static readonly ConcurrentDictionary<string, PendingOpen> Opening = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each module being opened was first seen loaded, and whether its tab was brought to the front.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Loaded, bool Activated)> LoadedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The modules being opened whose reference check was cancelled.</summary>
    private static readonly ConcurrentDictionary<string, bool> CheckCancelled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The prompts the bridge answered, newest last, for status.</summary>
    private static readonly ConcurrentQueue<object> Answered = new();

    private static int watching;

    private sealed record Dialog(string Title, bool IsMessage);

    /// <summary>Runs body on Avalonia's UI thread and returns what it returns; throws when the thread doesn't get to it in time.</summary>
    private static T OnUiThread<T>(Func<T> body, int timeoutMilliseconds = 3000)
    {
        var dispatcherType = Reflect.FindType("Avalonia.Threading.Dispatcher", "Avalonia.Base");
        var dispatcher = Reflect.Static(dispatcherType, "UIThread") ?? throw new BridgeException("Avalonia's UI dispatcher isn't there.");
        if (Reflect.Call(dispatcher, "CheckAccess") is true)
        {
            return body();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action action = () =>
        {
            try
            {
                done.TrySetResult(body());
            }
            catch (Exception ex)
            {
                done.TrySetException(Reflect.Unwrap(ex));
            }
        };
        (dispatcherType.GetMethod("InvokeAsync", [typeof(Action)]) ?? throw new BridgeException("Dispatcher.InvokeAsync(Action) is missing"))
            .Invoke(dispatcher, [action]);
        try
        {
            return done.Task.Wait(timeoutMilliseconds) ? done.Task.Result : throw new BridgeException("Service Studio's UI thread didn't answer in time.");
        }
        catch (AggregateException ex)
        {
            throw Reflect.Unwrap(ex);
        }
    }

    /// <summary>Service Studio's windows (call on the UI thread).</summary>
    private static List<object> Windows()
    {
        var app = Reflect.Static(Reflect.FindType("Avalonia.Application", "Avalonia.Controls"), "Current") ?? throw new BridgeException("Avalonia hasn't started.");
        var lifetime = Reflect.Get(app, "ApplicationLifetime") ?? throw new BridgeException("Avalonia has no application lifetime.");
        return Reflect.Enumerate(Reflect.Get(lifetime, "Windows"));
    }

    private static bool IsMessageDialog(object window) => window.GetType().FullName == "ServiceStudio.MessageDialog";

    /// <summary>
    /// Service Studio's dialog windows: its native message dialog, the web-view dialog windows its
    /// questions and progress windows use (ServiceStudio.WebViewImplementation.DialogWindow, which
    /// have no owner), and any other window with an owner.
    /// </summary>
    private static bool IsDialog(object window) =>
        IsMessageDialog(window)
        || window.GetType().Name.EndsWith("DialogWindow", StringComparison.Ordinal)
        || Reflect.TryGet(window, "Owner") != null;

    private static string? MessageTitle(object window) => Reflect.TryGet(Reflect.TryGet(window, "DataContext"), "Title") as string;

    private static string WindowTitle(object window) =>
        (IsMessageDialog(window) ? MessageTitle(window) : null) is { Length: > 0 } title ? title : Reflect.TryGet(window, "Title") as string ?? "(untitled)";

    /// <summary>The dialogs open in Service Studio.</summary>
    private static List<Dialog> OpenDialogs() => OnUiThread(() => Windows()
        .Where(w => Reflect.TryGet(w, "IsVisible") is true && IsDialog(w))
        .Select(w => new Dialog(WindowTitle(w), IsMessageDialog(w)))
        .ToList());

    /// <summary>Every Service Studio window, for status: what the dialog checks look at, so a miss can be told apart.</summary>
    private static object? WindowList()
    {
        try
        {
            return OnUiThread(() => Windows()
                .Select(w => (object)new
                {
                    Title = WindowTitle(w),
                    Kind = w.GetType().FullName,
                    Visible = Reflect.TryGet(w, "IsVisible") is true,
                    Owned = Reflect.TryGet(w, "Owner") != null,
                    Dialog = IsDialog(w),
                    Progress = IsDialog(w) && !IsMessageDialog(w)
                        ? ProgressModules(w).Select(m => ProgressState(m)).Select(s => (object)new { s.Message, s.CancelEnabled }).ToList()
                        : null,
                })
                .ToList());
        }
        catch (Exception ex)
        {
            return Reflect.Unwrap(ex).Message;
        }
    }

    /// <summary>The open dialogs, or null when the bridge can't tell.</summary>
    private static List<Dialog>? TryOpenDialogs()
    {
        try
        {
            return OpenDialogs();
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't list Service Studio's dialogs: {Reflect.Unwrap(ex).Message}");
            return null;
        }
    }

    /// <summary>
    /// The prompt Service Studio shows when a module it opens has incompatible or missing
    /// dependencies, as worded in its UI language: an entry of the Presenter's message catalogue,
    /// whose resource reads "Id|Type|Title|Text|…" (call on the UI thread, which has that culture).
    /// </summary>
    private static (string Title, string Text) MissingDependenciesPrompt()
    {
        const string name = "Reference_IncompatibleOrMissingDetected";
        var idType = Reflect.FindType("ServiceStudio.Presenter.PresenterMessageId", "ServiceStudio.Presenter");
        var id = idType.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new BridgeException($"PresenterMessageId.{name} is missing");
        var baseName = Reflect.TryGet(id, "ResourcesBaseName") as string ?? "ServiceStudio.Presenter.Generated.PresenterMessageIdResources.Generated";
        var key = Reflect.TryGet(id, "IdInResourceBaseName") as string ?? name;
        var entry = new ResourceManager(baseName, idType.Assembly).GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new BridgeException($"Service Studio's message catalogue has no {key}");
        var parts = entry.Split('|');
        return parts.Length >= 4 && parts[2].Length > 0 && parts[3].Length > 0
            ? (parts[2], parts[3])
            : throw new BridgeException($"Service Studio's {key} message isn't in the form the bridge reads");
    }

    /// <summary>The members the dialog watch reads, for status to check without a dialog.</summary>
    private static void DialogMembers()
    {
        _ = Reflect.FindType("Avalonia.Application", "Avalonia.Controls").GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
            ?? throw new BridgeException("Avalonia's Application.Current is missing");
        _ = Reflect.FindType("Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime", "Avalonia.Controls").GetProperty("Windows")
            ?? throw new BridgeException("Avalonia's window list is missing");
        var dispatcher = Reflect.FindType("Avalonia.Threading.Dispatcher", "Avalonia.Base");
        _ = (dispatcher.GetProperty("UIThread", BindingFlags.Public | BindingFlags.Static), dispatcher.GetMethod("InvokeAsync", [typeof(Action)])) is ({ }, { })
            ? 0 : throw new BridgeException("Avalonia's UI dispatcher is missing");
        _ = Reflect.FindType("ServiceStudio.MessageDialog", "ServiceStudio");
        _ = MissingDependenciesPrompt();
        // ProgressView's public MainModule (its base's, typed IViewModule) returns its ProgressViewModule.
        _ = Reflect.FindType("ServiceStudio.Presenter.ITopLevelPresenter", "ServiceStudio.Presenter").GetMethod("Activate", Type.EmptyTypes)
            ?? throw new BridgeException("ITopLevelPresenter.Activate is missing");
        var progress = Reflect.FindType("ServiceStudio.WebViewImplementation.ProgressView", "ServiceStudio.WebViewImplementation");
        _ = progress.GetProperty("MainModule", BindingFlags.Public | BindingFlags.Instance) ?? throw new BridgeException("ProgressView.MainModule is missing");
        var module = Reflect.FindType("ServiceStudio.WebViewImplementation.ProgressViewModule", "ServiceStudio.WebViewImplementation");
        _ = (module.GetField("Cancel", BindingFlags.NonPublic | BindingFlags.Instance), module.GetField("GetProgressInfo", BindingFlags.NonPublic | BindingFlags.Instance),
            module.GetProperty("IsCancelEnabled"), Reflect.FindType("Avalonia.LogicalTree.LogicalExtensions", "Avalonia.Base").GetMethod("GetLogicalDescendants"))
            is ({ }, { }, { }, { }) ? 0 : throw new BridgeException("a member the bridge reads to cancel the reference check is missing");
    }

    /// <summary>
    /// For a few minutes after openModule, handles the reference check Service Studio runs once the
    /// module has loaded: cancels its progress window ("Getting references from …"), and closes the
    /// missing-dependencies prompt when it comes anyway, bringing the module's tab to the front so
    /// a prompt held for it shows. Either answer leaves the references as the module saved them,
    /// unchecked against the server. Every other dialog is left for the user.
    /// </summary>
    private static void WatchForMissingDependencies(string module, string? host, string? key)
    {
        Opening[module] = new PendingOpen(DateTime.UtcNow, host, key);
        LoadedAt.TryRemove(module, out _);
        CheckCancelled.TryRemove(module, out _);
        if (Interlocked.Exchange(ref watching, 1) == 0)
        {
            _ = Task.Run(WatchLoop);
        }
    }

    private static async Task WatchLoop()
    {
        try
        {
            while (true)
            {
                foreach (var (module, pending) in Opening)
                {
                    if (DateTime.UtcNow - pending.Since > PromptWatch)
                    {
                        Opening.TryRemove(module, out _);
                        LoadedAt.TryRemove(module, out _);
                        CheckCancelled.TryRemove(module, out _);
                    }
                }

                if (Opening.IsEmpty)
                {
                    break;
                }

                try
                {
                    CancelReferenceCheck();
                    AnswerMissingDependencies();
                    BringLoadedTabsForward();
                }
                catch (Exception ex)
                {
                    Log.Info($"Watching for the missing-dependencies prompt: {Reflect.Unwrap(ex).Message}");
                }

                await Task.Delay(500);
            }
        }
        finally
        {
            Interlocked.Exchange(ref watching, 0);
            if (!Opening.IsEmpty && Interlocked.Exchange(ref watching, 1) == 0)
            {
                _ = Task.Run(WatchLoop);   // a module came in as the loop was ending
            }
        }
    }

    /// <summary>
    /// Cancels the progress window of the reference check that follows an open, as its Cancel button
    /// does: Service Studio's progress view (ProgressView, a public web-view control) raises its
    /// module's Cancel handler. Service Studio's text is encrypted in its assemblies, so the check is
    /// told apart by what holds in any language: the module's tab has loaded (the download and load
    /// progress windows come before that), and the progress names the environment's host.
    /// </summary>
    private static void CancelReferenceCheck()
    {
        var cancelled = OnUiThread(() =>
        {
            var loaded = LoadedPendingModules();
            var done = new List<(string Message, string Module)>();
            foreach (var window in Windows().Where(w => IsDialog(w) && !IsMessageDialog(w) && Reflect.TryGet(w, "IsVisible") is true))
            {
                var modules = ProgressModules(window);
                foreach (var module in modules)
                {
                    var (message, cancelEnabled) = ProgressState(module);
                    // Service Studio checks one module's references at a time, after it loads: the check
                    // is the newest loaded module's whose check hasn't been cancelled yet.
                    var match = loaded
                        .Where(l => !string.IsNullOrEmpty(l.Host) && message.Contains(l.Host, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(l => CheckCancelled.ContainsKey(l.Module))
                        .ThenByDescending(l => LoadedAt.TryGetValue(l.Module, out var seen) ? seen.Loaded : DateTime.MaxValue)
                        .FirstOrDefault();
                    var cancel = PrivateField(module, "Cancel") as Delegate;
                    if (match.Module != null && cancelEnabled && cancel != null)
                    {
                        cancel.DynamicInvoke();
                        CheckCancelled[match.Module] = true;
                        done.Add((message, match.Module));
                        continue;
                    }

                    Describe(module, $"Progress \"{message}\" left running: cancel {(cancelEnabled ? "enabled" : "disabled")}, handler {(cancel != null ? "found" : "missing")}, "
                        + $"loaded modules being opened: {(loaded.Count == 0 ? "none" : string.Join(", ", loaded.Select(l => l.Module)))}");
                }

                if (modules.Count == 0)
                {
                    Describe(window, $"Dialog \"{WindowTitle(window)}\" ({window.GetType().Name}) has no progress module; its web views host "
                        + string.Join(", ", HostedModules(window).Select(m => m.GetType().Name).Distinct().Take(25)));
                }
            }

            return done;
        });
        foreach (var (message, module) in cancelled)
        {
            Log.Info($"Cancelled Service Studio's \"{message}\" while opening {module}");
            Remember(message, [module], "Cancelled as its Cancel button does: the module's references weren't checked against the server.");
        }
    }

    private static void Describe(object subject, string text)
    {
        if (Described.TryGetValue(subject, out _))
        {
            return;
        }

        Described.Add(subject, text);
        Log.Info($"While opening {string.Join(", ", Opening.Keys)}: {text}");
    }

    /// <summary>The pending modules whose tab has loaded (call on the UI thread), matched by key or name.</summary>
    private static List<(string Module, string? Host)> LoadedPendingModules() =>
        LoadedPendingTabs().Select(t => (t.Module, t.Host)).ToList();

    /// <summary>The pending modules whose tab has loaded, with the tab (call on the UI thread).</summary>
    private static List<(string Module, string? Host, object Tab)> LoadedPendingTabs()
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var tabs = Reflect.Enumerate(Reflect.TryGet(Reflect.Static(runtimeType, "Instance"), "Aggregators"))
            .Select(a => (Tab: a, ESpace: Reflect.TryGet(a, "OwnerESpace")))
            .Where(t => t.ESpace != null)
            .Select(t => (t.Tab, Name: Reflect.TryGet(t.ESpace, "Name") as string, Key: Reflect.TryGet(t.ESpace, "Key")?.ToString()))
            .ToList();
        return Opening
            .Select(p => (p.Key, p.Value.Host, Tab: tabs.FirstOrDefault(t => string.Equals(t.Name, p.Key, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(p.Value.Key) && string.Equals(t.Key, p.Value.Key, StringComparison.OrdinalIgnoreCase))).Tab))
            .Where(p => p.Tab != null)
            .Select(p => (p.Key, p.Host, p.Tab!))
            .ToList();
    }

    /// <summary>
    /// Service Studio holds a module's missing-dependencies prompt until its tab is selected, and
    /// the opens queued after it wait meanwhile. So a couple of seconds after a module being opened
    /// has loaded (time for its reference check to be cancelled), its tab is brought to the front
    /// once, as a click on it does (ITopLevelPresenter.Activate): a prompt it was holding then
    /// shows, and the watch closes it.
    /// </summary>
    private static void BringLoadedTabsForward()
    {
        var activated = OnUiThread(() =>
        {
            var done = new List<string>();
            foreach (var (module, _, tab) in LoadedPendingTabs())
            {
                var seen = LoadedAt.GetOrAdd(module, _ => (DateTime.UtcNow, false));
                if (seen.Activated || DateTime.UtcNow - seen.Loaded < TimeSpan.FromSeconds(2))
                {
                    continue;
                }

                LoadedAt[module] = (seen.Loaded, true);
                Reflect.Call(tab, "Activate");
                done.Add(module);
            }

            return done;
        });
        foreach (var module in activated)
        {
            Log.Info($"Brought {module}'s tab to the front, so a prompt Service Studio holds for it can show");
        }
    }

    private const string ProgressModuleType = "ServiceStudio.WebViewImplementation.ProgressViewModule";

    /// <summary>A window's descendants in its logical and visual trees (call on the UI thread).</summary>
    private static List<object> Descendants(object window)
    {
        IEnumerable<object> Of(string extensions, string method, string parameter)
        {
            try
            {
                var type = Reflect.FindType(extensions, "Avalonia.Base");
                var parameterType = Reflect.FindType(parameter, "Avalonia.Base");
                return Reflect.Enumerate(type.GetMethod(method, [parameterType])?.Invoke(null, [window]));
            }
            catch (Exception ex)
            {
                Log.Info($"Couldn't walk a window's {method}: {Reflect.Unwrap(ex).Message}");
                return [];
            }
        }

        return Of("Avalonia.LogicalTree.LogicalExtensions", "GetLogicalDescendants", "Avalonia.LogicalTree.ILogical")
            .Concat(Of("Avalonia.VisualTree.VisualExtensions", "GetVisualDescendants", "Avalonia.Visual"))
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// The progress view modules a window shows. A progress window is a dialog view (DialogView)
    /// whose web-view render (ReactViewRender) hosts the ProgressViewModule as a child module, in
    /// its ChildViewModules (weak references) and Frames; a ProgressView control would hold it as
    /// its MainModule. All of them are looked through, in both trees.
    /// </summary>
    private static List<object> ProgressModules(object window) => HostedModules(window)
        .Where(o => o.GetType().FullName == ProgressModuleType)
        .ToList();

    /// <summary>Every view module a window's web views host: their main modules, child modules and frames' components.</summary>
    private static List<object> HostedModules(object window) => Descendants(window)
        .SelectMany(d => new[] { d, Reflect.TryGet(d, "MainModule") }.Concat(ChildModules(d)))
        .Where(o => o != null)
        .Select(o => o!)
        .Distinct(ReferenceEqualityComparer.Instance)
        .ToList();

    /// <summary>A web-view render's child modules and frame components (ReactViewRender keeps them in non-public properties).</summary>
    private static IEnumerable<object?> ChildModules(object render)
    {
        if (render.GetType().Name != "ReactViewRender")
        {
            yield break;
        }

        if (PrivateField(render, "<ChildViewModules>k__BackingField") is System.Collections.IDictionary children)
        {
            foreach (var reference in children.Values)
            {
                var arguments = new object?[] { null };
                if (reference?.GetType().GetMethod("TryGetTarget")?.Invoke(reference, arguments) is true)
                {
                    yield return arguments[0];
                }
            }
        }

        if (PrivateField(render, "<Frames>k__BackingField") is System.Collections.IDictionary frames)
        {
            foreach (var frame in frames.Values)
            {
                yield return frame == null ? null : Reflect.TryGet(frame, "Component") ?? PrivateField(frame, "<Component>k__BackingField");
            }
        }
    }

    /// <summary>What a progress module shows: its main message, and whether Cancel is enabled.</summary>
    private static (string Message, bool CancelEnabled) ProgressState(object module)
    {
        var info = (PrivateField(module, "GetProgressInfo") as Delegate)?.DynamicInvoke();
        return (Reflect.TryGet(info, "mainMessage") as string ?? string.Empty,
            Reflect.TryGet(module, "IsCancelEnabled") is true && Reflect.TryGet(info, "isCancelEnabled") is not false);
    }

    /// <summary>The progress windows already described in the log, so each is described once.</summary>
    private static readonly ConditionalWeakTable<object, object> Described = new();

    /// <summary>A private field, by name, on the type or a base: the progress module keeps its event handlers in fields named after the events.</summary>
    private static object? PrivateField(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } field)
            {
                return field.GetValue(target);
            }
        }

        return null;
    }

    private static void Remember(string what, List<string> modules, string note)
    {
        Answered.Enqueue(new
        {
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Prompt = what,
            WhileOpening = modules,
            Note = note,
        });
        while (Answered.Count > 20 && Answered.TryDequeue(out _))
        {
        }
    }

    /// <summary>Closes the prompt as its close button does: Service Studio takes that as not now, and Manage Dependencies doesn't open.</summary>
    private static void AnswerMissingDependencies()
    {
        var closed = OnUiThread(() =>
        {
            var (title, text) = MissingDependenciesPrompt();
            var firstLine = Squash(text.Split('\n')[0]);
            // The title is the prompt's own, from the catalogue. The native message dialog also shows
            // its text, which must match; the web-view dialogs keep theirs in the page.
            var found = Windows()
                .Where(w => IsDialog(w) && Reflect.TryGet(w, "IsVisible") is true && Squash(WindowTitle(w)) == Squash(title))
                .Where(w => !IsMessageDialog(w)
                    || Squash(Reflect.TryGet(Reflect.TryGet(w, "DataContext"), "MessageText") as string).Contains(firstLine, StringComparison.Ordinal))
                .ToList();
            foreach (var window in found)
            {
                Reflect.Call(window, "Close");
            }

            return found.Count > 0 ? title : null;
        });
        if (closed == null)
        {
            return;
        }

        var modules = Opening.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Info($"Answered Service Studio's \"{closed}\" prompt while opening {string.Join(", ", modules)}");
        Remember(closed, modules, "Closed as its close button does, so Manage Dependencies didn't open: the module has incompatible or missing dependencies to fix.");
    }

    /// <summary>Closes the visible dialogs with this exact title, as their close button does; returns how many (call off the UI thread).</summary>
    private static int CloseDialogsTitled(string title) => OnUiThread(() =>
    {
        var found = Windows()
            .Where(w => IsDialog(w) && Reflect.TryGet(w, "IsVisible") is true && Squash(WindowTitle(w)) == Squash(title))
            .ToList();
        foreach (var window in found)
        {
            Reflect.Call(window, "Close");
        }

        return found.Count;
    });

    private static string Squash(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
