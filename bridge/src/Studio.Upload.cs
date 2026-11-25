using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// Uploading open modules to the environment without publishing them, as Service Studio's own
/// Upload command does: a new version on the server, nothing compiled or deployed.
/// </summary>
internal static partial class Studio
{
    /// <summary>
    /// The message Service Studio shows when the server already has the module's version (nothing
    /// changed since it was opened or last uploaded): its title is fixed in Service Studio's server
    /// communications (UploadSkippedMessage), its text comes from the server. It has only Close.
    /// </summary>
    private const string UploadSkippedTitle = "Upload Skipped";

    /// <summary>
    /// Service Studio's Upload command (ServiceStudio.Presenter.Commands.Upload, a public command
    /// class): its registered instance, and its public CanExecute and Execute(target, presenter).
    /// </summary>
    private static (object Command, MethodInfo CanExecute, MethodInfo Execute) UploadCommand()
    {
        var (instance, canExecute, execute, _) = UploadCommandMembers();
        return (instance.GetValue(null) ?? throw new BridgeException("Service Studio's Upload command has no registered instance"), canExecute, execute);
    }

    /// <summary>The members alone: reading Instance runs Service Studio's registry, which only Service Studio should.</summary>
    private static (FieldInfo Instance, MethodInfo CanExecute, MethodInfo Execute, FieldInfo Abort) UploadCommandMembers()
    {
        var type = Reflect.FindType("ServiceStudio.Presenter.Commands.Upload", "ServiceStudio.Presenter");
        var instance = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?? throw new BridgeException("Service Studio's Upload command has no Instance");
        MethodInfo Find(string name) => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 2)
            ?? throw new BridgeException($"Service Studio's Upload command has no {name}(target, presenter)");
        var execute = Find("Execute");
        var abort = execute.ReturnType.GetField("AbortCommandResult", BindingFlags.Public | BindingFlags.Static)
            ?? throw new BridgeException("CommandResult.AbortCommandResult is missing");
        return (instance, Find("CanExecute"), execute, abort);
    }

    /// <summary>The modules open in Service Studio on this environment, by name, with their tabs.</summary>
    private static List<(string Name, object Tab)> OpenModules(Connection connection)
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var runtime = Reflect.Static(runtimeType, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        return Reflect.Enumerate(Reflect.TryGet(runtime, "Aggregators"))
            .Where(a => string.Equals(
                Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(a, "ServerCommunicationsProvider"), "LoginInfo"), "HostInfo"), "HostName") as string,
                connection.Host,
                StringComparison.OrdinalIgnoreCase))
            .Select(a => (Name: Reflect.TryGet(Reflect.TryGet(a, "OwnerESpace"), "Name") as string, Tab: a))
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => (m.Name!, m.Tab))
            .ToList();
    }

    public static object UploadModules(string? host, string? modules)
    {
        var connection = Pick(host);
        var open = OpenModules(connection);
        var wanted = modules?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            ?? open.Select(m => TabId(m.Tab) ?? m.Name).ToList();
        if (wanted.Count == 0)
        {
            throw new BridgeException($"No module is open in Service Studio on {connection.Host}.");
        }

        var (command, canExecute, execute) = UploadCommand();
        var environment = Modules(connection);
        var results = new List<object>();
        foreach (var name in wanted)
        {
            results.Add(UploadOne(connection, open, environment, command, canExecute, execute, name));
        }

        return Scoped(connection, new
        {
            Uploaded = results.Count(r => Reflect.TryGet(r, "Status") as string == "uploaded"),
            Modules = results,
            Note = "Uploaded, not published: each is a new version on the server (getModuleVersions lists it), and the published version is unchanged.",
        });
    }

    private static object UploadOne(Connection connection, List<(string Name, object Tab)> open, List<object> environment,
        object command, MethodInfo canExecute, MethodInfo execute, string name)
    {
        var tabs = MatchTabs(open.Select(m => m.Tab), name);
        if (tabs.Count != 1)
        {
            return new { Module = name, Status = "failed", Error = tabs.Count == 0 ? "It isn't open in Service Studio." : Ambiguous(name, tabs).Message };
        }

        var tab = tabs[0];
        var eSpace = Reflect.TryGet(tab, "OwnerESpace");
        name = Reflect.TryGet(eSpace, "Name") as string ?? name;
        string? warning = null;
        if (eSpace != null && EditSessionOn(eSpace, out _) == true)
        {
            warning = "Service Studio's own server has an edit session on it: changes that session hasn't merged aren't in this upload.";
        }

        // The version history before and after: the server is what says whether a version was added.
        var found = environment.FirstOrDefault(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
        int? Latest() => found == null ? null : Versions(connection, found).FirstOrDefault()?.Version;
        var before = Latest();

        // The upload waits on Service Studio's "Upload Skipped" message, which only says the server
        // already has this version: close it while the upload runs, and report the module unchanged.
        var skipped = 0;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Add(ref skipped, CloseDialogsTitled(UploadSkippedTitle));
                }
                catch (Exception ex)
                {
                    Log.Info($"Watching for {UploadSkippedTitle}: {Reflect.Unwrap(ex).Message}");
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
        });
        try
        {
            object? result = null;
            try
            {
                InContext(tab, $"Uploading {name}", () =>
                {
                    if (canExecute.Invoke(command, [tab, tab]) is not true)
                    {
                        throw new BridgeException("Service Studio's Upload command isn't available for it now (as when Upload is greyed out in Service Studio).");
                    }

                    result = execute.Invoke(command, [tab, tab]);
                }, wait: true);
            }
            finally
            {
                stop.Cancel();
                watch.Wait(TimeSpan.FromSeconds(5));
            }

            if (Volatile.Read(ref skipped) > 0)
            {
                Log.Info($"Closed Service Studio's \"{UploadSkippedTitle}\" message while uploading {name}");
                return new { Module = name, Status = "unchanged", Version = before, Previous = before, Warning = warning,
                    Error = (object)"Service Studio skipped the upload: the server already has this version (nothing changed since it was opened or last uploaded). The bridge closed its Upload Skipped message." };
            }

            if (found == null)
            {
                found = Modules(connection).FirstOrDefault(m => string.Equals(Reflect.TryGet(m, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
            }

            var after = Latest();
            // A command that gives up returns the shared AbortCommandResult.
            var failed = result != null && ReferenceEquals(result, UploadCommandMembers().Abort.GetValue(null));
            return after != null && after != before
                ? new { Module = name, Status = "uploaded", Version = after, Previous = before, Warning = warning }
                : failed
                    ? new { Module = name, Status = "failed", Version = before, Previous = before, Warning = warning, Error = (object)"Service Studio reported the upload as failed: look at its messages." }
                    : new { Module = name, Status = "unchanged", Version = before, Previous = before, Warning = warning, Error = (object)"No new version on the server: Service Studio found nothing to upload, or skipped it." };
        }
        catch (Exception ex)
        {
            return new { Module = name, Status = "failed", Version = before, Previous = before, Warning = warning, Error = Reflect.Unwrap(ex).Message };
        }
    }

    /// <summary>The members uploadModules reads, for status to check without a module.</summary>
    private static void UploadMembers() => _ = UploadCommandMembers();
}
