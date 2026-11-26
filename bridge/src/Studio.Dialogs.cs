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

    /// <summary>How long the watch stays on a module whose tab is still busy with no dialog showing (something held for it).</summary>
    private static readonly TimeSpan PromptHold = TimeSpan.FromHours(1);

    /// <summary>How long a tab's queue may take to run an empty action before the bridge counts it busy, as Service Studio's own server does.</summary>
    private static readonly TimeSpan BusyProbe = TimeSpan.FromSeconds(1);

    /// <summary>How many times the watch brings a module's tab to the front, its first time included.</summary>
    private const int MaxActivations = 8;

    /// <summary>
    /// A module openModule is opening: when it was asked, the environment's host, the module's key,
    /// and whether only the missing-dependencies prompt is to be closed (a tab already open, which
    /// openModule brought to the front because something was held for it).
    /// </summary>
    private sealed record PendingOpen(DateTime Since, string? Host, string? Key, bool PromptOnly = false);

    /// <summary>The modules openModule is opening, by name.</summary>
    private static readonly ConcurrentDictionary<string, PendingOpen> Opening = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How a module being opened is getting on since its tab loaded (the watch loop alone changes it).</summary>
    private sealed class OpenProgress
    {
        /// <summary>When its tab was first seen loaded.</summary>
        public DateTime Loaded { get; init; }

        /// <summary>How many times its tab was brought to the front, and when last.</summary>
        public int Activations { get; set; }

        public DateTime LastActivated { get; set; }

        /// <summary>An empty action queued on its tab, which runs once the tab is idle, and when it was queued.</summary>
        public Task? Probe { get; set; }

        public DateTime ProbeStarted { get; set; }

        /// <summary>Its tab's queue didn't run the latest probe within BusyProbe.</summary>
        public bool Busy { get; set; }

        /// <summary>Its tab listed no modal dialog when last looked at: busy then means something is held until the tab is in front.</summary>
        public bool NothingShowing { get; set; }

        public bool Held => Busy && NothingShowing;
    }

    /// <summary>The modules being opened whose tab has loaded, by name.</summary>
    private static readonly ConcurrentDictionary<string, OpenProgress> Progress = new(StringComparer.OrdinalIgnoreCase);

    private const string MessagesModuleType = "ServiceStudio.WebViewImplementation.MessagesViewModule";

    /// <summary>The ids of the messages the reference check can show, once resolved.</summary>
    private static HashSet<string>? referenceCheckMessages;

    /// <summary>One of Service Studio's messages: its id, its type (Error, Warning, Question…) and its text.</summary>
    private sealed record Message(string? Id, string Type, string Text);

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

    /// <summary>
    /// Every Service Studio window, for status: what the dialog checks look at, so a miss can be told
    /// apart. A dialog says which module's tab it belongs to, and a message its id and type.
    /// </summary>
    private static object? WindowList()
    {
        try
        {
            return OnUiThread(() =>
            {
                var owners = TryDialogOwners();
                return Windows()
                    .Select(w => (object)new
                    {
                        Title = WindowTitle(w),
                        Kind = w.GetType().FullName,
                        Visible = Reflect.TryGet(w, "IsVisible") is true,
                        Owned = Reflect.TryGet(w, "Owner") != null,
                        Dialog = IsDialog(w),
                        Tab = owners.GetValueOrDefault(w),
                        Message = IsDialog(w) && !IsMessageDialog(w) && MessageOf(w) is { } message ? new { message.Id, message.Type } : null,
                        Progress = IsDialog(w) && !IsMessageDialog(w)
                            ? ProgressModules(w).Select(m => ProgressState(m)).Select(s => (object)new { s.Message, s.CancelEnabled }).ToList()
                            : null,
                    })
                    .ToList();
            });
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
        // Service Studio's messages: the dialogs each tab has open, and what a message view shows.
        _ = (DialogsOfView(modal: true), DialogsOfView(modal: false));
        _ = Reflect.FindType("ServiceStudio.Presenter.AbstractPresenter", "ServiceStudio.Presenter").GetProperty("View")
            ?? throw new BridgeException("AbstractPresenter.View is missing");
        _ = ReferenceCheckMessageIds();
        var messages = Reflect.FindType(MessagesModuleType, "ServiceStudio.WebViewImplementation");
        _ = (messages.GetProperty("TelemetryData"), PrivateFieldInfo(messages, "GetInitialMessagesInfo")) is ({ }, { })
            ? 0 : throw new BridgeException("a member the bridge reads to recognise Service Studio's messages is missing");
        // ProgressView's public MainModule (its base's, typed IViewModule) returns its ProgressViewModule.
        _ = Reflect.FindType("ServiceStudio.Presenter.ITopLevelPresenter", "ServiceStudio.Presenter").GetMethod("Activate", Type.EmptyTypes)
            ?? throw new BridgeException("ITopLevelPresenter.Activate is missing");
        var progress = Reflect.FindType("ServiceStudio.WebViewImplementation.ProgressView", "ServiceStudio.WebViewImplementation");
        _ = progress.GetProperty("MainModule", BindingFlags.Public | BindingFlags.Instance) ?? throw new BridgeException("ProgressView.MainModule is missing");
        var module = Reflect.FindType("ServiceStudio.WebViewImplementation.ProgressViewModule", "ServiceStudio.WebViewImplementation");
        _ = (module.GetField("Cancel", BindingFlags.NonPublic | BindingFlags.Instance), module.GetField("GetProgressInfo", BindingFlags.NonPublic | BindingFlags.Instance),
            module.GetProperty("IsCancelEnabled"), Reflect.FindType("Avalonia.LogicalTree.LogicalExtensions", "Avalonia.Base").GetMethod("GetLogicalDescendants"))
            is ({ }, { }, { }, { }) ? 0 : throw new BridgeException("a member the bridge reads to cancel the reference check is missing");
        // ITopLevelPresenter.ExecuteInContext(Action, executeWhenTopLevelIsIdle): how a tab is told busy.
        _ = Reflect.FindType("ServiceStudio.Presenter.ITopLevelPresenter", "ServiceStudio.Presenter").GetMethod("ExecuteInContext", [typeof(Action), typeof(bool)]) is { ReturnType: var returns }
            && typeof(Task).IsAssignableFrom(returns)
            ? 0 : throw new BridgeException("ITopLevelPresenter.ExecuteInContext(Action, bool), which tells a tab busy, is missing");
    }

    /// <summary>
    /// Queues an empty action on a tab to run once it's idle, as Service Studio's own server does to
    /// tell a tab busy: it hasn't run within BusyProbe while a dialog or an operation holds the tab.
    /// Null when the bridge can't queue one.
    /// </summary>
    private static Task? ProbeTab(object tab)
    {
        try
        {
            return Reflect.Call(tab, "ExecuteInContext", (Action)(() => { }), true) as Task;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref probeFailed, 1) == 0)
            {
                Log.Info($"Couldn't tell whether a tab is busy: {Reflect.Unwrap(ex).Message}");
            }

            return null;
        }
    }

    private static int probeFailed;

    /// <summary>Whether these probes haven't run within BusyProbe, waiting for all of them at once; null where there's no probe.</summary>
    private static List<bool?> StillBusy(List<Task?> probes)
    {
        var deadline = DateTime.UtcNow + BusyProbe;
        foreach (var probe in probes)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                probe?.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            catch (Exception)
            {
                // A probe that failed has run: the tab isn't busy.
            }
        }

        return probes.Select(p => p == null ? (bool?)null : !p.IsCompleted).ToList();
    }

    /// <summary>
    /// Whether a tab is held: busy for over a second with no modal dialog listed for it, which is how
    /// a prompt or progress window Service Studio holds until the tab is in front looks.
    /// </summary>
    private static bool IsHeld(object tab) =>
        StillBusy([ProbeTab(tab)])[0] is true && OnUiThread(() => TabDialogs(tab, modal: true).Count == 0);

    /// <summary>
    /// Every module tab, for status: whether it's busy (its queue didn't run an empty action within a
    /// second, as Service Studio's own server tells it) and the modal dialogs listed for it. A tab
    /// busy with none listed is one Service Studio holds something for until it's in front.
    /// </summary>
    private static object? TabList()
    {
        try
        {
            var tabs = OnUiThread(() => Aggregators()
                .Select(a => (Tab: a,
                    Module: Reflect.TryGet(Reflect.TryGet(a, "OwnerESpace"), "Name") as string ?? "(loading)",
                    Dialogs: TabDialogs(a, modal: true).Select(WindowTitle).ToList()))
                .ToList());
            var busy = StillBusy(tabs.Select(t => ProbeTab(t.Tab)).ToList());
            return tabs.Select((t, i) => (object)new
                {
                    t.Module,
                    TabId = TabId(t.Tab),
                    Busy = busy[i],
                    t.Dialogs,
                    Note = busy[i] is true && t.Dialogs.Count == 0
                        ? "Busy with no dialog showing: Service Studio is most likely holding a prompt for this tab until it's in front. "
                            + "openModule on this module brings the tab forward (and closes the missing-dependencies prompt if that's what shows); "
                            + "or ask the user to select the tab."
                        : null,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            return Reflect.Unwrap(ex).Message;
        }
    }

    /// <summary>Service Studio's module tabs, loading ones included (call on the UI thread).</summary>
    private static List<object> Aggregators() =>
        Reflect.Enumerate(Reflect.TryGet(Reflect.Static(Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter"), "Instance"), "Aggregators"));

    /// <summary>
    /// For a few minutes after openModule, handles the reference check Service Studio runs once the
    /// module has loaded: cancels its progress window ("Getting references from …"), and closes the
    /// missing-dependencies prompt when it comes anyway, bringing the module's tab to the front so
    /// a prompt held for it shows. Either answer leaves the references as the module saved them,
    /// unchecked against the server. The check's other messages in the module's tab (the
    /// communication error a cancelled check can leave, "not logged in") are closed too, as their
    /// close button does. Every other dialog is left for the user. While the module's tab stays busy
    /// with no dialog showing (a prompt held until the tab is in front, which a later open can take
    /// from it), the tab is brought forward again, less often each time, and the watch goes on for up
    /// to an hour. With promptOnly (a tab already open, brought forward
    /// by openModule because something was held for it), only the missing-dependencies prompt is
    /// closed, nothing is cancelled, and the watch ends after a few minutes.
    /// </summary>
    private static void WatchForMissingDependencies(string module, string? host, string? key, bool promptOnly = false)
    {
        var now = DateTime.UtcNow;
        Opening[module] = new PendingOpen(now, host, key, promptOnly);
        Progress.TryRemove(module, out _);
        if (promptOnly)
        {
            Progress[module] = new OpenProgress { Loaded = now, Activations = 1, LastActivated = now };
        }

        CheckCancelled.TryRemove(module, out _);
        StartWatch();
    }

    private static void EndOpenWatch(string module)
    {
        Opening.TryRemove(module, out _);
        Progress.TryRemove(module, out _);
        CheckCancelled.TryRemove(module, out _);
    }

    /// <summary>A module convertModule is converting: since when, its environment, and the ids of the messages to close in its tab.</summary>
    private sealed record PendingConversion(DateTime Since, string? Host, HashSet<string> Ids);

    /// <summary>The modules convertModule is converting, by name.</summary>
    private static readonly ConcurrentDictionary<string, PendingConversion> Converting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a few minutes after convertModule, closes the messages Service Studio shows in the
    /// converted module's tab with these ids (its "publish now?" question), as their close button does.
    /// </summary>
    private static void WatchConversion(string module, string? host, HashSet<string> ids)
    {
        Converting[module] = new PendingConversion(DateTime.UtcNow, host, ids);
        StartWatch();
    }

    private static void StartWatch()
    {
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
                    var age = DateTime.UtcNow - pending.Since;
                    var held = !pending.PromptOnly && Progress.TryGetValue(module, out var progress) && progress.Held;
                    if (age > PromptHold || (age > PromptWatch && !held))
                    {
                        if (held)
                        {
                            Log.Info($"Stopped watching {module}: its tab is still busy with no dialog showing an hour after openModule; "
                                + "Service Studio may be holding a prompt for it until the tab is selected");
                        }

                        EndOpenWatch(module);
                    }
                }

                foreach (var (module, pending) in Converting)
                {
                    if (DateTime.UtcNow - pending.Since > PromptWatch)
                    {
                        Converting.TryRemove(module, out _);
                    }
                }

                if (Opening.IsEmpty && Converting.IsEmpty)
                {
                    break;
                }

                try
                {
                    if (!Opening.IsEmpty)
                    {
                        CancelReferenceCheck();
                        // The reference check's messages, by id, in the tabs being opened; matching the
                        // missing-dependencies prompt by its title still catches it in any other window.
                        AnswerReferenceCheckMessages();
                        AnswerMissingDependencies();
                        ProbeLoadedTabs();
                        BringLoadedTabsForward();
                    }

                    if (!Converting.IsEmpty)
                    {
                        AnswerConversionMessages();
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"Watching Service Studio's dialogs: {Reflect.Unwrap(ex).Message}");
                }

                await Task.Delay(500);
            }
        }
        finally
        {
            Interlocked.Exchange(ref watching, 0);
            if ((!Opening.IsEmpty || !Converting.IsEmpty) && Interlocked.Exchange(ref watching, 1) == 0)
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
            // A tab already open, watched for a held prompt only, has no check of openModule's to cancel.
            var loaded = LoadedPendingModules().Where(l => Opening.TryGetValue(l.Module, out var p) && !p.PromptOnly).ToList();
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
                        .ThenByDescending(l => Progress.TryGetValue(l.Module, out var seen) ? seen.Loaded : DateTime.MaxValue)
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

                // A message is described where messages are handled, with its tab and id.
                if (modules.Count == 0 && !Described.TryGetValue(window, out _) && MessageOf(window) == null)
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
        var tabs = Aggregators()
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
    /// has loaded (time for its reference check to be cancelled), its tab is brought to the front,
    /// as a click on it does (ITopLevelPresenter.Activate): a prompt it was holding then shows, and
    /// the watch closes it. The check can end on its own, too fast to be seen, and its prompt come
    /// after another open has taken the front: so while the tab is held (busy, no dialog showing),
    /// it's brought forward again, 5 s after the last time and twice as long each time after that,
    /// up to MaxActivations times, one tab at a time.
    /// </summary>
    private static void BringLoadedTabsForward()
    {
        var activated = OnUiThread(() =>
        {
            var now = DateTime.UtcNow;
            var recently = Progress.Values.Any(p => now - p.LastActivated < TimeSpan.FromSeconds(3));
            var done = new List<(string Module, int Times)>();
            foreach (var (module, _, tab) in LoadedPendingTabs())
            {
                var progress = Progress.GetOrAdd(module, _ => new OpenProgress { Loaded = now });
                progress.NothingShowing = TabDialogs(tab, modal: true).Count == 0;
                var due = progress.Activations == 0
                    ? now - progress.Loaded >= TimeSpan.FromSeconds(2)
                    : Opening.TryGetValue(module, out var pending) && !pending.PromptOnly && progress.Held && !recently
                        && progress.Activations < MaxActivations && now - progress.LastActivated >= Backoff(progress.Activations);
                if (!due)
                {
                    continue;
                }

                progress.Activations++;
                progress.LastActivated = now;
                recently = true;
                Reflect.Call(tab, "Activate");
                done.Add((module, progress.Activations));
            }

            return done;
        });
        foreach (var (module, times) in activated)
        {
            Log.Info(times == 1
                ? $"Brought {module}'s tab to the front, so a prompt Service Studio holds for it can show"
                : $"Brought {module}'s tab to the front again ({times} times): it's busy with no dialog showing, so Service Studio is likely holding a prompt for it");
        }
    }

    /// <summary>How long after its last time a held tab is brought forward again: 5 s, then twice as long each time, at most 5 minutes.</summary>
    private static TimeSpan Backoff(int activations) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, activations - 1))));

    /// <summary>
    /// Tells, for each module being opened whose tab has loaded, whether the tab is busy, by queuing
    /// an empty action on it (one at a time, at most one a second) as Service Studio's own server
    /// does: the reference check and every prompt it shows run in the open's own command on that
    /// queue, so a prompt held for the tab keeps it busy.
    /// </summary>
    private static void ProbeLoadedTabs()
    {
        var loaded = OnUiThread(() => LoadedPendingTabs().Select(t => (t.Module, t.Tab)).ToList());
        var now = DateTime.UtcNow;
        foreach (var (module, progress) in Progress)
        {
            if (!loaded.Any(t => string.Equals(t.Module, module, StringComparison.OrdinalIgnoreCase)))
            {
                progress.Busy = false;   // its tab closed, or holds another module
            }
        }

        foreach (var (module, tab) in loaded)
        {
            if (!Progress.TryGetValue(module, out var progress))
            {
                continue;   // BringLoadedTabsForward starts following it
            }

            if (progress.Probe is { IsCompleted: false })
            {
                progress.Busy |= now - progress.ProbeStarted > BusyProbe;
                continue;
            }

            if (progress.Probe != null)
            {
                progress.Busy = false;
            }

            if (progress.Probe == null || now - progress.ProbeStarted >= BusyProbe)
            {
                progress.Probe = ProbeTab(tab);
                progress.ProbeStarted = now;
            }
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
    private static object? PrivateField(object target, string name) => PrivateFieldInfo(target.GetType(), name)?.GetValue(target);

    private static FieldInfo? PrivateFieldInfo(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } field)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Closes the messages of the reference check Service Studio runs once a module has loaded
    /// (ServerOperations.CheckForIncompatibleOrMissingReferences), in the tab of a module the bridge
    /// is opening, as their close button does. The check shows one of three: the missing-dependencies
    /// prompt when references are incompatible or missing; the server error its server call failed
    /// with (a communication error, as when a cancel cuts the call short); or "not logged in". Like
    /// every message, each is a modal dialog window for the tab, hosting Service Studio's Messages
    /// view (MessagesProvider.ShowMessage), that stops the tab's queue and the opens queued after it.
    /// Closing the window, rather than pressing a button, gives the message's own Cancel answer, which
    /// Service Studio takes as "not now". They're told apart by id, which no translation changes;
    /// every other message is left for the user.
    /// </summary>
    private static void AnswerReferenceCheckMessages()
    {
        var ids = ReferenceCheckMessageIds();
        var prompt = MissingDependenciesIds();
        var tabs = OnUiThread(() => LoadedPendingTabs().Select(t => (t.Module, t.Tab)).ToList());
        CloseMessages(tabs, module => Opening.TryGetValue(module, out var pending) && pending.PromptOnly ? prompt : ids, "opening", "it isn't the reference check's");
    }

    private static HashSet<string>? missingDependenciesIds;

    /// <summary>The ids the missing-dependencies prompt carries.</summary>
    private static HashSet<string> MissingDependenciesIds() =>
        missingDependenciesIds ??= new HashSet<string>(MessageIdTexts(PresenterMessage("Reference_IncompatibleOrMissingDetected")), StringComparer.Ordinal);

    /// <summary>
    /// Closes the messages convertModule registered for the converted module's tab (Service Studio's
    /// "publish now?" question after a conversion), as their close button does: No, not now.
    /// </summary>
    private static void AnswerConversionMessages()
    {
        var tabs = OnUiThread(() =>
        {
            var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
            return Reflect.Enumerate(Reflect.TryGet(Reflect.Static(runtimeType, "Instance"), "Aggregators"))
                .Select(a => (Module: Reflect.TryGet(Reflect.TryGet(a, "OwnerESpace"), "Name") as string, Tab: a))
                .Where(t => t.Module != null && Converting.ContainsKey(t.Module))
                .Select(t => (t.Module!, t.Tab))
                .ToList();
        });
        CloseMessages(tabs, module => Converting.TryGetValue(module, out var pending) ? pending.Ids : new HashSet<string>(), "converting", "it isn't the conversion's");
    }

    /// <summary>
    /// Closes, as their close button does, the messages in these tabs whose id the module's set has,
    /// and records each under AnsweredPrompts. Closing the window, rather than pressing a button, gives
    /// the message's own Cancel answer. Every other message is left for the user.
    /// </summary>
    private static void CloseMessages(List<(string Module, object Tab)> tabs, Func<string, IReadOnlySet<string>> ids, string doing, string leftBecause)
    {
        if (tabs.Count == 0)
        {
            return;
        }

        var closed = OnUiThread(() =>
        {
            var done = new List<(string Module, string Title, Message Message)>();
            foreach (var (module, tab) in tabs)
            {
                var ours = ids(module);
                foreach (var window in TabDialogs(tab, modal: true))
                {
                    if (MessageOf(window) is not { } message)
                    {
                        continue;
                    }

                    var title = WindowTitle(window);
                    if (message.Id == null || !ours.Contains(message.Id))
                    {
                        Describe(window, $"Message \"{title}\" ({message.Id}, {message.Type}) in {module}'s tab left for the user: {leftBecause}");
                        continue;
                    }

                    Reflect.Call(window, "Close");
                    done.Add((module, title, message));
                }
            }

            return done;
        });
        foreach (var (module, title, message) in closed)
        {
            Log.Info($"Closed Service Studio's \"{title}\" message ({message.Id}, {message.Type}) while {doing} {module}: {message.Text}");
            var text = message.Text.Length > 300 ? message.Text[..300] + "…" : message.Text;
            Remember(title, [module], $"Closed as its close button does, which gives the message's own Cancel answer. {message.Type}: {text}");
        }
    }

    /// <summary>
    /// The ids the reference check's messages carry (MessageDefinition.Id, the first field of each
    /// catalogue entry): the missing-dependencies prompt's, and every server-communication message's,
    /// which its server errors and "not logged in" come from (as do the debugger data's, also fetched
    /// while a module opens). Each is read from Service Studio's own
    /// catalogue, with the catalogue key too in case an entry's id differs from it.
    /// </summary>
    private static HashSet<string> ReferenceCheckMessageIds()
    {
        if (referenceCheckMessages != null)
        {
            return referenceCheckMessages;
        }

        var messageId = MessageIdType();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(object? id)
        {
            if (id != null)
            {
                ids.UnionWith(MessageIdTexts(id));
            }
        }

        Add(PresenterMessage("Reference_IncompatibleOrMissingDetected"));
        // Every message catalogue of Service Studio's server communications: ServerCommunicationsMessageId,
        // and DebuggerCommunicationsMessageId for the debugger data an open also fetches.
        var server = Reflect.FindType("ServiceStudio.ServerCommunications.API.ServerCommunicationsMessageId", "ServiceStudio.ServerCommunications.API");
        Type[] types;
        try
        {
            types = server.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        foreach (var catalogue in types.Where(t => t.IsClass && messageId.IsAssignableFrom(t)))
        {
            foreach (var field in catalogue.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == catalogue))
            {
                Add(field.GetValue(null));
            }
        }

        return ids.Count > 1 ? referenceCheckMessages = ids : throw new BridgeException("Service Studio's catalogue gave no ids for the reference check's messages");
    }

    private static Type MessageIdType() => Reflect.FindType("OutSystems.RuntimeCommon.MessageId", "OutSystems.RuntimeCommon");

    /// <summary>A message of Service Studio's own catalogue (a PresenterMessageId), by name.</summary>
    private static object PresenterMessage(string name) =>
        Reflect.FindType("ServiceStudio.Presenter.PresenterMessageId", "ServiceStudio.Presenter")
            .GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? throw new BridgeException($"PresenterMessageId.{name} is missing");

    /// <summary>A message's definition as Service Studio builds it from its catalogue: id, type, texts, buttons.</summary>
    private static object MessageDefinitionOf(object messageId)
    {
        var getMessage = Reflect.FindType("OutSystems.RuntimeCommon.MessageDefinition", "OutSystems.RuntimeCommon")
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(m => m.Name == "GetMessage" && m.GetParameters() is [{ } first, { ParameterType.IsArray: true }] && first.ParameterType == MessageIdType())
            ?? throw new BridgeException("MessageDefinition.GetMessage(MessageId, params string[]) is missing");
        return getMessage.Invoke(null, [messageId, Array.Empty<string>()]) ?? throw new BridgeException("Service Studio's catalogue has no such message");
    }

    /// <summary>
    /// The ids a shown message can carry for this catalogue entry (MessageDefinition.Id, the entry's
    /// first field), with the catalogue key too in case an entry's id differs from it.
    /// </summary>
    private static IEnumerable<string> MessageIdTexts(object messageId) =>
        new[] { Reflect.TryGet(MessageDefinitionOf(messageId), "Id"), Reflect.TryGet(messageId, "IdInResourceBaseName") }
            .OfType<string>()
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal);

    /// <summary>The message a dialog window shows, when it's one of Service Studio's messages (call on the UI thread).</summary>
    private static Message? MessageOf(object window)
    {
        var module = HostedModules(window).FirstOrDefault(m => m.GetType().FullName == MessagesModuleType);
        if (module == null)
        {
            return null;
        }

        // The view asks for what it shows through an event; MessagesProvider answers with the message.
        var info = (PrivateField(module, "GetInitialMessagesInfo") as Delegate)?.DynamicInvoke();
        var text = new[] { "message", "description" }.Select(n => Reflect.TryGet(info, n) as string).Where(s => !string.IsNullOrWhiteSpace(s));
        return new Message(
            Reflect.TryGet(Reflect.TryGet(module, "TelemetryData"), "area") as string,
            Reflect.TryGet(info, "messageType")?.ToString() ?? "Unknown",
            Squash(string.Join(" ", text)));
    }

    /// <summary>
    /// The dialog windows Service Studio has open for a tab (call on the UI thread): DialogWindow
    /// keeps them per tab view, modal and not. A dialog held until its tab comes to the front isn't
    /// among them until then.
    /// </summary>
    private static List<object> TabDialogs(object tab, bool modal)
    {
        var view = Reflect.TryGet(tab, "View");
        var method = DialogsOfView(modal);
        return view != null && method.GetParameters()[0].ParameterType.IsInstanceOfType(view)
            ? Reflect.Enumerate(method.Invoke(null, [view]))
            : [];
    }

    private static MethodInfo DialogsOfView(bool modal)
    {
        var name = modal ? "GetModalDialogsOpened" : "GetNonModalDialogsOpened";
        return Reflect.FindType("ServiceStudio.WebViewImplementation.DialogWindow", "ServiceStudio.WebViewImplementation")
                .GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == 1)
            ?? throw new BridgeException($"DialogWindow.{name} is missing");
    }

    /// <summary>Which module's tab each dialog window belongs to, for status (call on the UI thread); empty when the bridge can't tell.</summary>
    private static Dictionary<object, string> TryDialogOwners()
    {
        var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        try
        {
            var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
            foreach (var tab in Reflect.Enumerate(Reflect.TryGet(Reflect.Static(runtimeType, "Instance"), "Aggregators")))
            {
                var name = Reflect.TryGet(Reflect.TryGet(tab, "OwnerESpace"), "Name") as string ?? "(loading)";
                foreach (var window in TabDialogs(tab, modal: true).Concat(TabDialogs(tab, modal: false)))
                {
                    owners.TryAdd(window, name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't tell which tab each dialog belongs to: {Reflect.Unwrap(ex).Message}");
        }

        return owners;
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
