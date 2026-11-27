using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// Closing a module's tab, as the tab's Close command does (IAggregatorPresenter.Close). Service
/// Studio's own close asks whether to stop a server operation running on the module and whether to
/// save a modified one; the bridge asks nobody: it refuses in both cases, and drops unsaved changes
/// only when told to, through the close Service Studio uses when it must not ask (ForceClose).
/// </summary>
internal static partial class Studio
{
    private sealed record CloseApi(
        MethodInfo Close,
        MethodInfo ForceClose,
        PropertyInfo IsClosed,
        PropertyInfo OpenedESpaces,
        PropertyInfo Modified,
        PropertyInfo IsNew,
        PropertyInfo SavePath,
        MethodInfo StartedServerProcess,
        FieldInfo Verb);

    private static CloseApi? closeApi;

    /// <summary>The public Service Studio members closeModule reads and calls.</summary>
    private static CloseApi CloseMembers()
    {
        if (closeApi != null)
        {
            return closeApi;
        }

        var tab = Reflect.FindType("ServiceStudio.Presenter.IAggregatorPresenter", "ServiceStudio.Presenter");
        MethodInfo TabMethod(string name) => tab.GetMethod(name, Type.EmptyTypes) ?? throw new BridgeException($"IAggregatorPresenter.{name} is missing");
        var eSpace = Reflect.FindType("ServiceStudio.Model.ESpace", "ServiceStudio.Framework");
        PropertyInfo ESpaceProperty(string name) =>
            eSpace.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new BridgeException($"ESpace.{name} is missing");
        var process = Reflect.FindType("ServiceStudio.Presenter.Commands.ServerProcess", "ServiceStudio.Presenter");
        return closeApi = new CloseApi(
            TabMethod("Close"),
            TabMethod("ForceClose"),
            tab.GetProperty("IsClosed") ?? throw new BridgeException("IAggregatorPresenter.IsClosed is missing"),
            Reflect.FindType("ServiceStudio.Presenter.IBaseAggregatorPresenter", "ServiceStudio.Framework").GetProperty("OpenedESpaces")
                ?? throw new BridgeException("IBaseAggregatorPresenter.OpenedESpaces is missing"),
            ESpaceProperty("WasModifiedSinceLastSave"),
            ESpaceProperty("IsNewEspace"),
            ESpaceProperty("LastSavePath"),
            process.GetMethod("GetStartedESpaceServerProcess", BindingFlags.Public | BindingFlags.Static, [tab])
                ?? throw new BridgeException("ServerProcess.GetStartedESpaceServerProcess is missing"),
            process.GetField("ServerOperationVerb", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new BridgeException("ServerProcess.ServerOperationVerb is missing"));
    }

    /// <summary>
    /// Closes the tab of a module open in Service Studio. It refuses, asking nobody, when Service
    /// Studio's own server has an edit session or unmerged changes on the module, when the tab is busy
    /// (a dialog, or a prompt held until the tab is in front), when a server operation such as a
    /// publish is running on it, and, unless discardChanges, when the module has unsaved changes.
    /// A window's last module tab closes like any other: the window stays, on its start page.
    /// </summary>
    public static object CloseModule(string? host, string module, bool discardChanges)
    {
        var (connection, tab, name) = ResolveOpenModule(host, module);
        if (tab == null)
        {
            throw NotOpen(module, connection);
        }

        module = name;
        var tabId = TabId(tab);
        var eSpace = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");
        var api = CloseMembers();

        // Service Studio's own server checks a working copy out against the module in that tab: closing
        // the tab under a session leaves it nothing to merge into.
        string? warning = null;
        switch (McpSession(eSpace, out var sessionProblem))
        {
            case { Open: true }:
                throw new BridgeException($"Service Studio's MCP server has an edit session open on {module}. End it first (endOmlEditSession), which discards "
                    + "the edits it hasn't merged: merge what you want to keep (omlMerge) before. If you didn't start that session, ask the user first.");
            case { Unmerged: true }:
                throw new BridgeException($"{module} has changes from applyModelApiCode that aren't merged: merge them (omlMerge) or discard them (omlReset), "
                    + "then call closeModule again.");
            case null:
                warning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}). If one was open, its module has gone: end it.";
                break;
        }

        // A dialog, or a prompt Service Studio holds until the tab is in front, stops the tab's queue,
        // where the close would wait.
        if (StillBusy([ProbeTab(tab)])[0] is true)
        {
            var dialogs = OnUiThread(() => TabDialogs(tab, modal: true).Select(WindowTitle).ToList());
            throw new BridgeException(dialogs.Count > 0
                ? $"{module}'s tab has a dialog open ({string.Join("; ", dialogs.Select(d => $"\"{d}\""))}), and Service Studio runs nothing else in it "
                    + "until someone answers it: ask the user to answer it, then call closeModule again."
                : $"{module}'s tab is busy with no dialog showing: Service Studio is running something there, or holding a prompt for it until it's in front. "
                    + $"openModule on {module} brings a held tab forward (and closes the missing-dependencies prompt if that's what shows), or ask the user to "
                    + "select the tab; call closeModule again once status no longer lists the tab as busy.");
        }

        string? refusal = null;
        var discarded = false;
        var neverPublished = false;
        var closeAnswered = true;
        InContext(tab, $"Closing {module}", () =>
        {
            // Service Studio's close would ask whether to stop it.
            if (api.StartedServerProcess.Invoke(null, [tab]) is { } process)
            {
                refusal = $"A server operation ({api.Verb.GetValue(process) ?? "unnamed"}) is running on {module}, such as a publish: "
                    + "wait for it to end, then call closeModule again.";
                return;
            }

            // Service Studio's close would ask whether to save each modified module in the tab.
            var modified = Reflect.Enumerate(api.OpenedESpaces.GetValue(tab)).Where(e => api.Modified.GetValue(e) is true).ToList();
            neverPublished = modified.Any(e => api.IsNew.GetValue(e) is true);
            if (modified.Count > 0 && !discardChanges)
            {
                // A publish saves a module opened from a local file to that file first, and marks one opened
                // from the environment as saved once uploaded; an upload does only the second.
                var file = api.SavePath.GetValue(eSpace) as string;
                refusal = neverPublished
                    ? $"{module} has never been published, so it exists only in this tab: closing it loses the whole module. Publish it (omlPublish) when the "
                        + "user asks, or, only when the user says to drop it, call closeModule again with discardChanges: true."
                    : string.IsNullOrEmpty(file)
                        ? $"{module} has unsaved changes (merged ones included): closing loses them. Publish them (omlPublish) or upload them (uploadModules) "
                            + "when the user asks, or, only when the user says to drop them, call closeModule again with discardChanges: true."
                        : $"{module} has unsaved changes (merged ones included): closing loses them. It has a local file ({file}), which an upload "
                            + "doesn't save: publish them (omlPublish), which saves the file first, when the user asks, or ask the user to save it; "
                            + "only when the user says to drop them, call closeModule again with discardChanges: true.";
                return;
            }

            discarded = modified.Count > 0;
            if (discarded)
            {
                api.ForceClose.Invoke(tab, null);
            }
            else
            {
                closeAnswered = api.Close.Invoke(tab, null) is true;
            }
        }, wait: true);
        if (refusal != null)
        {
            throw new BridgeException(refusal);
        }

        if (!closeAnswered || api.IsClosed.GetValue(tab) is not true)
        {
            throw new BridgeException($"Service Studio didn't close {module}'s tab: look in Service Studio for a message, or ask the user to close it.");
        }

        EndOpenWatch(module);
        return Scoped(connection, new
        {
            Module = module,
            TabId = tabId,
            Status = "closed",
            Changes = discarded ? "discarded" : "none",
            Note = discarded
                ? neverPublished
                    ? "The module had never been published: it's gone, with its changes."
                    : "Its unsaved changes are gone; the server keeps the versions it had."
                : null,
            Warning = warning,
        });
    }
}
