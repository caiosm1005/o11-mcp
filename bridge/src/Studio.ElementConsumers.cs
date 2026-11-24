using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ServiceStudioBridge;

/// <summary>
/// The modules that reference one public element: the list Service Studio's Find Usages in all
/// Modules asks the server for before it searches each module. By default the bridge stops at the
/// list. The server takes the element itself, not its key: Service Studio passes the element of the
/// producer it has open, and the bridge does the same, or loads the producer's published version in
/// memory when it isn't open. An extension has no such element (it has no OML), so it isn't covered.
///
/// On request, or for a part of an element (a static record, an attribute, an entity or role
/// action), the bridge goes on as Find Usages in all Modules does: it loads each consumer (as open,
/// or its published version, in memory), finds its reference to the element, or to the part, and
/// counts what in the module refers to it. The server knows consumers per top-level element only,
/// so a part's are searched among its element's.
/// </summary>
internal static partial class Studio
{
    private sealed record ElementConsumersApi(MethodInfo Load, Type Referenceable, Type Container, PropertyInfo Children, MethodInfo GetConsumers);

    /// <summary>
    /// The public Service Studio members the usage count reads: what refers to an object, Find
    /// Usages' own test of whether a referer uses it, the properties and expression mentions it
    /// reports as usages, the parts it searches along with an element, and the global key that
    /// finds a producer's element among a consumer's references.
    /// </summary>
    private sealed record UsageApi(
        PropertyInfo Referers,
        PropertyInfo UserVisible,
        MethodInfo IsBeingUsed,
        MethodInfo GetParent,
        PropertyInfo DirectChildren,
        PropertyInfo Properties,
        MethodInfo PropertyVisible,
        MethodInfo PropertyValue,
        Type TypeRecordDefinition,
        PropertyInfo TypePropertyDescriptor,
        Type PropertyObject,
        PropertyInfo AssociatedProperty,
        PropertyInfo AsPropertyDescriptor,
        MethodInfo Locations,
        Type ListType,
        Type AnonymousAttribute,
        PropertyInfo AnonymousParent,
        Type? ObjectExpression,
        Type Argument,
        object ArgumentParameter,
        object LinkTarget,
        Type EventFilter,
        object EventFilterAttribute,
        Type[] Calls,
        Type NamedRecord,
        MethodInfo Attributes,
        Type Entity,
        PropertyInfo IdentifierType,
        PropertyInfo IsStaticEntity,
        MethodInfo StaticRecords,
        Type ElementAction,
        Type Role,
        PropertyInfo RoleException,
        MethodInfo KeyCreate,
        MethodInfo KeyFind);

    /// <summary>A part of an element, as Find Usages in all Modules finds its copy in a consumer's reference.</summary>
    private sealed record PartTarget(string Kind, string Name, string Collection, string? OriginalKey, object? ActionKind)
    {
        public string Describe => $"{Name} ({Kind.ToLowerInvariant()})";
    }

    /// <summary>One module to search: its name and key, and its tab when it's open in Service Studio.</summary>
    private sealed record ConsumerToSearch(string Name, object Key, object? Open);

    /// <summary>What a consumer's search found: its usages, where it was read, or why it couldn't be.</summary>
    private sealed record UsageResult(int Usages, string ReadFrom, string? Problem);

    /// <summary>The part kinds Find Usages in all Modules takes below a top-level element, as partKind names them.</summary>
    private static readonly string[] PartKinds = ["Record", "Attribute", "Action"];

    // Service Studio downloads four consumers at a time for Find Usages in all Modules, and loads two.
    private const int SearchDownloads = 4;
    private const int SearchLoads = 2;
    private const int SearchTimeoutSeconds = 1200;

    // An MCP client gives up on a call after about a minute: a search still running by then is collected with job.
    private const int CallBudgetSeconds = 45;
    private const int JobRetentionMinutes = 10;

    /// <summary>A usage search, under way or done: a call returns its result once done, else its progress and id.</summary>
    private sealed class UsageJob(string request, Connection connection, string element, string? part, int total)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public string Request { get; } = request;
        public Connection Connection { get; } = connection;
        public string Element { get; } = element;
        public string? Part { get; } = part;
        public int Total { get; } = total;
        public DateTime Started { get; } = DateTime.UtcNow;
        public int Done;
        public volatile bool Waiting = true;
        public DateTime? Finished;
        public Task<Func<int?, object>>? Work;
    }

    private static readonly ConcurrentDictionary<string, UsageJob> usageJobs = new();
    private static readonly SemaphoreSlim searchSlot = new(1, 1);
    private static readonly SemaphoreSlim loadSlot = new(SearchLoads, SearchLoads);

    public static object GetElementConsumers(string? host, string? element, string? producer, string? kind, string? part, string? partKind, bool? countUsages,
        string? job, int? limit)
    {
        var started = DateTime.UtcNow;
        foreach (var old in usageJobs.Values.Where(j => j.Finished < started.AddMinutes(-JobRetentionMinutes)))
        {
            usageJobs.TryRemove(old.Id, out _);
        }

        if (!string.IsNullOrWhiteSpace(job))
        {
            var running = usageJobs.GetValueOrDefault(job.Trim())
                ?? throw new BridgeException($"No search job {job}: it finished over {JobRetentionMinutes} minutes ago, or Service Studio restarted since. "
                    + "Call getElementConsumers with element (and part) to search again.");
            return Collect(running, CallBudgetSeconds, limit);
        }

        if (string.IsNullOrWhiteSpace(element))
        {
            throw new BridgeException("element is required (or job, to collect a search's result).");
        }

        // Arguments first: these checks need no Service Studio.
        string? wantedPartKind = null;
        if (partKind != null)
        {
            wantedPartKind = PartKinds.FirstOrDefault(k => string.Equals(k, partKind, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException($"partKind is Record, Attribute or Action, not {partKind}.");
            if (string.IsNullOrWhiteSpace(part))
            {
                throw new BridgeException("partKind needs a part: the name of the record, attribute or action.");
            }
        }

        part = string.IsNullOrWhiteSpace(part) ? null : part.Trim();
        if (part != null && countUsages == false)
        {
            throw new BridgeException("The server lists consumers of top-level elements only, so a part's consumers come from searching each consumer of its "
                + "element: part always counts usages. Leave countUsages out.");
        }

        var counting = part != null || countUsages == true;
        // The same search asked again while it runs (a client that gave up on the first call) joins it.
        var request = string.Join("|", host, element, producer, kind, part, wantedPartKind).ToLowerInvariant();
        if (counting && usageJobs.Values.FirstOrDefault(j => j.Request == request && j.Finished == null) is { } same)
        {
            return Collect(same, CallBudgetSeconds, limit);
        }

        var connection = Pick(host);
        var matches = PublicElementsNamed(connection, element, producer, kind);
        if (matches.Count == 0)
        {
            throw new BridgeException($"No module exposes a public element named {element}"
                + (kind != null ? $" of kind {kind}" : string.Empty) + (producer != null ? $" in {producer}" : string.Empty)
                + ". searchPublicElements finds names that are close."
                + (part == null ? " For a static record, an attribute or an entity action, name its entity as element and the record, attribute or action as part." : string.Empty));
        }

        if (matches.Count > 1)
        {
            throw new BridgeException($"{matches.Count} public elements match: {string.Join("; ", matches.Select(DescribeElement))}. "
                + "Pass the producer (and kind, if one producer has several) of the one you mean.");
        }

        var match = matches[0];
        if (Reflect.TryGet(match, "IsExtension") is true)
        {
            throw new BridgeException($"{DescribeElement(match)} comes from an extension, which the bridge can't cover. Service Studio's Find Usages in all "
                + "Modules, which this tool follows, asks the server about an element of a module it has loaded, and an extension has no OML to load; "
                + "the server's module-level consumer list (getConsumers) has none for extensions either.");
        }

        var api = ElementConsumersMembers();
        var usage = counting ? UsageMembers() : null;
        var service = ApplicationService(connection);
        var producerKey = Reflect.Get(match, "ApplicationKey") ?? throw new BridgeException("The public element has no producer key.");
        var elementId = KeyId(Reflect.Get(match, "Key")) ?? throw new BridgeException("The public element has no key.");
        var producerId = KeyId(producerKey);
        // The service's own name for the producer (ServiceCenter, not (System)), as Service Studio's tab shows it.
        var producerModule = Reflect.TryGet(match, "ApplicationName") as string ?? string.Empty;
        var elementName = Reflect.TryGet(match, "Name") as string ?? element;

        // Everything the search needs from the producer's element is read while it's loaded: the
        // server's consumer list, the element's global key, and the part.
        (List<object> Consumers, object? Key, PartTarget? Part) Read(object found) => (
            Reflect.Enumerate(api.GetConsumers.Invoke(service, [found])),
            usage?.KeyCreate.Invoke(null, [found]),
            part == null ? null : FindPart(found, elementName, DescribeElement(match), part, wantedPartKind));

        // As Service Studio does: the producer as open, when it is (any version holds the element's key).
        (List<object> Consumers, object? Key, PartTarget? Part)? read = null;
        var readFrom = $"{producerModule} as open in Service Studio";
        var tab = OpenTab(connection, producerModule);
        if (Reflect.TryGet(tab, "OwnerESpace") is { } open && KeyId(Reflect.TryGet(open, "Key")) == producerId)
        {
            read = Run(() =>
            {
                // A read of the live module may meet an edit under way: then the published version is read instead.
                object? found = null;
                try
                {
                    found = FindReferenceable(api, open, elementId);
                }
                catch (Exception ex)
                {
                    Log.Info($"getElementConsumers: reading {producerModule} as open failed ({Reflect.Unwrap(ex).Message}); loading its published version");
                }

                return found == null ? ((List<object>, object?, PartTarget?)?)null : Read(found);
            });
        }

        if (read == null)
        {
            readFrom = $"{producerModule}'s published version, loaded in memory";
            var (_, bytes) = Run(() => Download(connection, 0, producerKey), timeoutSeconds: 300);
            read = Run(() =>
            {
                var eSpace = api.Load.Invoke(null, [bytes, null, null, null, null, null, null])
                    ?? throw new BridgeException($"Service Studio loaded nothing from {producerModule}'s OML.");
                try
                {
                    var found = FindReferenceable(api, eSpace, elementId)
                        ?? throw new BridgeException($"{producerModule}'s published version has no {DescribeElement(match)}: it may be in a version not "
                            + "published yet. Ask the user to open the module in Service Studio, or publish it, then try again.");
                    return Read(found);
                }
                finally
                {
                    (eSpace as IDisposable)?.Dispose();
                }
            });
        }

        var (consumers, elementKey, partTarget) = read.Value;
        var modules = Modules(connection);
        if (!counting)
        {
            return Scoped(connection, new
            {
                Element = DescribeElement(match),
                ReadFrom = readFrom,
                Note = "The server's list, from the consumers' published versions: a module with unpublished changes to its references can be missing, "
                    + "or listed when it no longer uses the element. countUsages: true searches each one and counts its usages.",
                Consumers = Page(ConsumerRows(consumers, modules), limit, null),
            });
        }

        var search = StartUsageSearch(connection, usage!, match, readFrom, consumers, elementKey!, partTarget, producerId, modules, request);
        return Collect(search, Math.Max(5, CallBudgetSeconds - (int)(DateTime.UtcNow - started).TotalSeconds), limit);
    }

    /// <summary>
    /// The part of the element named part, among those Find Usages in all Modules goes down to: an
    /// entity's static records, attributes and entity actions (only Get, when the entity is exposed
    /// read-only), a structure's attributes, a role's actions. An entity action is also found by its
    /// verb (Create for CreateCustomer).
    /// </summary>
    private static PartTarget FindPart(object found, string elementName, string described, string part, string? wantedKind)
    {
        var candidates = new List<(string Kind, object Part, string Collection)>();
        candidates.AddRange(Reflect.Enumerate(Reflect.TryCall(found, "GetStaticRecords")).Select(r => ("Record", r, "StaticRecords")));
        candidates.AddRange(Reflect.Enumerate(Reflect.TryGet(found, "EntityAttributes") ?? Reflect.TryGet(found, "StructureAttributes")).Select(a => ("Attribute", a, "Attributes")));
        var readOnly = Reflect.TryGet(found, "ExposeReadOnly") is true;
        candidates.AddRange(Reflect.Enumerate(Reflect.TryGet(found, "EntityActions"))
            .Where(a => !readOnly || a.GetType().Name == "GetEntity")
            .Select(a => ("Action", a, "EntityActions")));
        candidates.AddRange(Reflect.Enumerate(Reflect.TryGet(found, "RoleActions")).Select(a => ("Action", a, "RoleActions")));
        if (candidates.Count == 0)
        {
            throw new BridgeException($"{described} has no parts Find Usages in all Modules searches for: it goes below the top level only for an entity's static "
                + "records, attributes and entity actions, a structure's attributes and a role's actions. Leave part out to search for the element itself.");
        }

        string NameOf(object o) => Reflect.TryGet(o, "Name") as string ?? string.Empty;
        var named = candidates
            .Where(c => wantedKind == null || c.Kind == wantedKind)
            .Where(c => string.Equals(NameOf(c.Part), part, StringComparison.OrdinalIgnoreCase)
                || (c.Kind == "Action" && string.Equals(NameOf(c.Part), part + elementName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (named.Count == 0)
        {
            var available = candidates
                .Where(c => wantedKind == null || c.Kind == wantedKind)
                .GroupBy(c => c.Kind)
                .Select(g =>
                {
                    var names = g.Select(c => NameOf(c.Part)).Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    return $"{g.Key.ToLowerInvariant()}s: {string.Join(", ", names.Take(80))}{(names.Count > 80 ? $" and {names.Count - 80} more" : string.Empty)}";
                });
            throw new BridgeException($"{described} has no {(wantedKind ?? "part").ToLowerInvariant()} named {part}. It has {string.Join("; ", available)}."
                + (readOnly ? " It's exposed read-only, so consumers can use only its Get action." : string.Empty));
        }

        if (named.Count > 1)
        {
            throw new BridgeException($"{described} has {named.Count} parts named {part}: "
                + string.Join(", ", named.Select(c => $"a {c.Kind.ToLowerInvariant()}")) + ". Pass partKind (Record, Attribute or Action).");
        }

        var (partKind, target, collection) = named[0];
        // Service Studio finds a record or an attribute in a consumer by the producer's key, which its
        // copy keeps as OriginalKey, and an action by its kind (each entity or role has one of each).
        return new PartTarget(partKind, NameOf(target), collection,
            partKind == "Action" ? null : KeyId(Reflect.TryGet(target, "Key")),
            partKind == "Action" ? Reflect.TryGet(target, "AbstractObjectKind") : null);
    }

    /// <summary>
    /// Find Usages in all Modules without its results pane, started in the background: each consumer
    /// on the server's list, plus the modules open in Service Studio that reference the element
    /// although their published version doesn't, searched for the element (or its part), with how
    /// many places in each use it. Those that use it come first; those that don't stay listed, with 0.
    /// One search runs at a time, apart from the bridge's other calls.
    /// </summary>
    private static UsageJob StartUsageSearch(Connection connection, UsageApi usage, object match, string readFrom, List<object> consumers, object elementKey,
        PartTarget? part, string? producerId, List<object> modules, string request)
    {
        var toSearch = new List<ConsumerToSearch>();
        var listed = new HashSet<string>();
        foreach (var consumer in consumers)
        {
            var id = KeyId(Reflect.TryGet(consumer, "Key"));
            if (id != null && Reflect.TryGet(consumer, "Key") is { } key && listed.Add(id))
            {
                toSearch.Add(new ConsumerToSearch(Reflect.TryGet(consumer, "Name") as string ?? id, key, null));
            }
        }

        // As Service Studio does: an open module is searched as open, unsaved changes included, and one
        // the server doesn't list (its reference isn't published yet) is added when it has the reference.
        var addedFromTabs = new List<string>();
        foreach (var tab in OpenTabs(connection))
        {
            if (Reflect.TryGet(tab, "OwnerESpace") is not { } eSpace || KeyId(Reflect.TryGet(eSpace, "Key")) is not { } id || id == producerId)
            {
                continue;
            }

            var index = toSearch.FindIndex(c => KeyId(c.Key) == id);
            if (index >= 0)
            {
                toSearch[index] = toSearch[index] with { Open = eSpace };
                continue;
            }

            try
            {
                if (usage.KeyFind.Invoke(elementKey, [eSpace]) != null && Reflect.TryGet(eSpace, "Key") is { } key)
                {
                    var name = Reflect.TryGet(eSpace, "Name") as string ?? id;
                    toSearch.Add(new ConsumerToSearch(name, key, eSpace));
                    addedFromTabs.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"getElementConsumers: checking open {Reflect.TryGet(eSpace, "Name")} failed ({Reflect.Unwrap(ex).Message})");
            }
        }

        var job = new UsageJob(request, connection, DescribeElement(match), part?.Describe, toSearch.Count);
        job.Work = Task.Run(() =>
        {
            searchSlot.Wait();
            try
            {
                job.Waiting = false;
                var found = new ConcurrentDictionary<string, UsageResult>();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(SearchTimeoutSeconds));
                try
                {
                    Parallel.ForEach(toSearch, new ParallelOptions { MaxDegreeOfParallelism = SearchDownloads, CancellationToken = cancel.Token }, c =>
                    {
                        found[KeyId(c.Key)!] = SearchConsumer(connection, usage, c, elementKey, part);
                        Interlocked.Increment(ref job.Done);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Reported in the result: the consumers without one weren't searched in time.
                }

                return UsageOutcome(match, readFrom, toSearch, addedFromTabs, new Dictionary<string, UsageResult>(found), part, modules);
            }
            finally
            {
                job.Finished = DateTime.UtcNow;
                searchSlot.Release();
            }
        });
        usageJobs[job.Id] = job;
        return job;
    }

    /// <summary>The search's result, once done, else its progress and the job id that collects it.</summary>
    private static object Collect(UsageJob job, int seconds, int? limit)
    {
        bool done;
        try
        {
            done = job.Work!.Wait(TimeSpan.FromSeconds(seconds));
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (done)
        {
            return Scoped(job.Connection, job.Work.Result(limit));
        }

        var elapsed = (int)(DateTime.UtcNow - job.Started).TotalSeconds;
        return Scoped(job.Connection, new
        {
            Element = job.Element,
            Part = job.Part,
            Status = job.Waiting ? "Waiting" : "Searching",
            Searched = job.Done,
            Of = job.Total,
            Seconds = elapsed,
            Job = job.Id,
            Note = (job.Waiting ? "Waiting for another search to finish first. " : $"Searched {job.Done} of {job.Total} consumers in {elapsed} s. ")
                + $"It goes on in the background: call getElementConsumers with job \"{job.Id}\" (nothing else) for the result; each call waits up to "
                + $"{CallBudgetSeconds} s for it. Other bridge calls can run meanwhile.",
        });
    }

    /// <summary>The result of a finished search, paged as each call asks.</summary>
    private static Func<int?, object> UsageOutcome(object match, string readFrom, List<ConsumerToSearch> toSearch, List<string> addedFromTabs,
        Dictionary<string, UsageResult> results, PartTarget? part, List<object> modules)
    {
        var applicationByKey = new Dictionary<string, string?>();
        foreach (var m in modules)
        {
            if (KeyId(Reflect.TryGet(m, "Key")) is { } id)
            {
                applicationByKey.TryAdd(id, Reflect.TryGet(m, "ApplicationName") as string);
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        var notSearched = new List<object>();
        foreach (var c in toSearch.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = KeyId(c.Key)!;
            if (!results.TryGetValue(id, out var result))
            {
                notSearched.Add(new { Name = c.Name, Reason = $"not searched within {SearchTimeoutSeconds} s" });
                continue;
            }

            if (result.Problem != null)
            {
                notSearched.Add(new { Name = c.Name, Reason = result.Problem });
                continue;
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["Name"] = c.Name,
                ["Key"] = id,
                ["Application"] = applicationByKey.GetValueOrDefault(id),
                ["Usages"] = result.Usages,
                ["ReadFrom"] = result.ReadFrom,
            });
        }

        // Those that use it first, so a limited page shows them; then those that don't (0), each by name.
        rows = rows.OrderBy(r => (int)r["Usages"]! == 0).ToList();
        var using_ = rows.Count(r => (int)r["Usages"]! > 0);
        var target = part == null ? DescribeElement(match) : $"{part.Describe} of {DescribeElement(match)}";
        return limit => new
        {
            Element = DescribeElement(match),
            Part = part?.Describe,
            ReadFrom = readFrom,
            Searched = toSearch.Count,
            Using = using_,
            Note = (part == null
                    ? "Each consumer searched, as Find Usages in all Modules does; those that use the element come first. Usages 0: the module "
                        + "references the element but nothing in it uses it (an unused dependency), or its reference is out of date. "
                    : $"The server lists consumers of {DescribeElement(match)} only; each was searched for {part.Describe}, as Find Usages in all Modules "
                        + "does; those that use it come first. Usages 0: the module uses the element but not this part, or its reference is out of date. ")
                + "A module is read as open in Service Studio, unsaved changes included, or as its published version, loaded in memory (nothing is "
                + "written). Usages counts what Find Usages lists: each mention in an expression, each other property that points at it, a call and "
                + "what reads its result. A reference entity's own settings (Identifier, Label, Order By, indexes) aren't counted for an attribute, though "
                + "Find Usages lists them.",
            AddedFromOpenTabs = addedFromTabs.Count > 0 ? addedFromTabs : null,
            Consumers = Page(rows, limit, null),
            Unchecked = notSearched.Count > 0 ? notSearched : null,
            UncheckedNote = notSearched.Count > 0 ? $"These couldn't be searched, so whether they use {target} is unknown." : null,
        };
    }

    /// <summary>The tabs open in windows signed in to the connection's environment.</summary>
    private static IEnumerable<object> OpenTabs(Connection connection)
    {
        var runtimeType = Reflect.FindType("ServiceStudio.Presenter.RuntimeImplementation", "ServiceStudio.Presenter");
        var runtime = Reflect.Static(runtimeType, "Instance") ?? throw new BridgeException("Service Studio hasn't finished starting.");
        return Reflect.Enumerate(Reflect.TryGet(runtime, "Aggregators")).Where(a => string.Equals(
            Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(Reflect.TryGet(a, "ServerCommunicationsProvider"), "LoginInfo"), "HostInfo"), "HostName") as string,
            connection.Host,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One consumer's usages: as open, when it is and can be read, else its published version loaded
    /// in memory, as Find Usages in all Modules downloads it.
    /// </summary>
    private static UsageResult SearchConsumer(Connection connection, UsageApi usage, ConsumerToSearch consumer, object elementKey, PartTarget? part)
    {
        if (consumer.Open != null)
        {
            try
            {
                return new UsageResult(CountIn(usage, consumer.Open, elementKey, part), "open in Service Studio", null);
            }
            catch (Exception ex)
            {
                // An edit under way can change the module while it's read: the published version is read instead.
                Log.Info($"getElementConsumers: reading {consumer.Name} as open failed ({Reflect.Unwrap(ex).Message}); loading its published version");
            }
        }

        try
        {
            var api = ElementConsumersMembers();
            var (_, bytes) = Download(connection, 0, consumer.Key);
            loadSlot.Wait();
            try
            {
                var eSpace = api.Load.Invoke(null, [bytes, null, null, null, null, null, null])
                    ?? throw new BridgeException("Service Studio loaded nothing from its OML.");
                try
                {
                    return new UsageResult(CountIn(usage, eSpace, elementKey, part), "published version", null);
                }
                finally
                {
                    (eSpace as IDisposable)?.Dispose();
                }
            }
            finally
            {
                loadSlot.Release();
            }
        }
        catch (Exception ex)
        {
            var e = Reflect.Unwrap(ex);
            Log.Info($"getElementConsumers: searching {consumer.Name} failed ({e.GetType().Name}: {e.Message})");
            return new UsageResult(0, string.Empty, e.Message);
        }
    }

    /// <summary>
    /// How many places in the module use the element or its part: none when the module has no
    /// reference to it, or holds it out of sight (Find Usages skips what isn't user-visible).
    /// </summary>
    private static int CountIn(UsageApi usage, object eSpace, object elementKey, PartTarget? part)
    {
        var reference = usage.KeyFind.Invoke(elementKey, [eSpace]);
        if (reference == null)
        {
            return 0;
        }

        var target = part == null
            ? reference
            : Reflect.Enumerate(Reflect.TryGet(reference, part.Collection)).FirstOrDefault(p => part.ActionKind != null
                ? Equals(Reflect.TryGet(p, "AbstractObjectKind"), part.ActionKind)
                : KeyId(Reflect.TryGet(p, "OriginalKey")) == part.OriginalKey);
        if (target == null || usage.UserVisible.GetValue(target) is not true)
        {
            return 0;
        }

        // A reference entity's own Identifier, Label, Order By and indexes point at its attributes: Find Usages
        // lists them for an attribute, but they're the producer's definition, not the consumer's use.
        var search = new UsageSearch(usage, target, part != null ? reference : null);
        foreach (var referer in search.ReferersOf(target))
        {
            if (usage.Calls.Any(t => t.IsInstanceOfType(referer)))
            {
                // A call (or an aggregate's source), then what reads its result, as Find Usages nests them.
                var before = search.Count;
                search.Visit(referer, false, target, 0);
                if (search.Count > before)
                {
                    foreach (var reader in search.ReferersOf(referer))
                    {
                        search.Visit(reader, false, referer, 1);
                    }
                }

                continue;
            }

            // An expression typed by the element (not mentioning it) isn't a usage.
            var typedOnly = usage.ObjectExpression?.IsInstanceOfType(referer) == true && ReferenceEquals(Reflect.TryGet(referer, "ExpressionType"), target);
            search.Visit(referer, typedOnly, target, 0);
        }

        // Find Usages searches an entity's or structure's attributes with it, an entity's identifier and static
        // records, its entity actions, and a role's exception.
        if (usage.NamedRecord.IsInstanceOfType(target))
        {
            foreach (var attribute in Reflect.Enumerate(usage.Attributes.Invoke(target, null)))
            {
                search.VisitReferers(attribute);
            }

            if (usage.Entity.IsInstanceOfType(target))
            {
                if (usage.IdentifierType.GetValue(target) is { } identifier)
                {
                    foreach (var referer in search.ReferersOf(identifier))
                    {
                        var parent = usage.GetParent.Invoke(referer, null);
                        if (usage.AnonymousAttribute.IsInstanceOfType(referer) || parent == null || usage.UserVisible.GetValue(parent) is true)
                        {
                            search.Visit(referer, true, identifier, 0);
                        }
                    }
                }

                if (usage.IsStaticEntity.GetValue(target) is true)
                {
                    foreach (var record in Reflect.Enumerate(usage.StaticRecords.Invoke(target, null)))
                    {
                        search.VisitReferers(record);
                    }
                }
            }
        }

        foreach (var action in Reflect.Enumerate(usage.DirectChildren.GetValue(target)).Where(usage.ElementAction.IsInstanceOfType))
        {
            search.VisitReferers(action);
        }

        if (usage.Role.IsInstanceOfType(target) && usage.RoleException.GetValue(target) is { } roleException)
        {
            search.VisitReferers(roleException);
        }

        return search.Count;
    }

    /// <summary>
    /// The results Find Usages would list for one object of a consumer, counted: each mention in an
    /// expression or other text, and each other property that points at it, once. A list of it or a
    /// field of an anonymous record isn't a usage itself: what uses those is.
    /// </summary>
    private sealed class UsageSearch(UsageApi usage, object source, object? owner)
    {
        private readonly HashSet<(object Owner, object Property, int Start, int Length)> found = new(new ResultComparer());

        public int Count => found.Count;

        public List<object> ReferersOf(object refered) => Reflect.Enumerate(usage.Referers.GetValue(refered));

        public void VisitReferers(object refered)
        {
            foreach (var referer in ReferersOf(refered))
            {
                Visit(referer, false, refered, 0);
            }
        }

        /// <param name="typedOnly">The referer has the refered as its type: an expression of that type doesn't count.</param>
        public void Visit(object referer, bool typedOnly, object refered, int depth)
        {
            if (depth > 8 || IsInsideOwner(referer) || usage.IsBeingUsed.Invoke(null, [source, referer, refered]) is not true)
            {
                return;
            }

            if (usage.AnonymousAttribute.IsInstanceOfType(referer))
            {
                foreach (var next in ReferersOf(referer))
                {
                    Visit(next, typedOnly, referer, depth + 1);
                }

                if (usage.AnonymousParent.GetValue(referer) is { } record)
                {
                    foreach (var next in ReferersOf(record))
                    {
                        Visit(next, typedOnly, record, depth + 1);
                    }
                }

                return;
            }

            if (usage.ListType.IsInstanceOfType(referer))
            {
                foreach (var next in ReferersOf(referer))
                {
                    Visit(next, typedOnly, referer, depth + 1);
                }

                return;
            }

            if (usage.PropertyObject.IsInstanceOfType(referer))
            {
                var property = usage.AsPropertyDescriptor.GetValue(usage.AssociatedProperty.GetValue(referer));
                var parent = usage.GetParent.Invoke(referer, null);
                if (typedOnly || property == null || parent == null || !(Visible(property, parent) || IsArgumentParameter(parent, property)))
                {
                    return;
                }

                foreach (var location in Reflect.Enumerate(usage.Locations.Invoke(referer, [refered])))
                {
                    found.Add((parent, property, Reflect.TryGet(location, "SelectionStart") as int? ?? -1, Reflect.TryGet(location, "SelectionLength") as int? ?? -1));
                }

                return;
            }

            foreach (var listed in Reflect.Enumerate(usage.Properties.GetValue(referer)))
            {
                var property = usage.TypeRecordDefinition.IsInstanceOfType(listed) ? usage.TypePropertyDescriptor.GetValue(listed) ?? listed : listed;
                if ((Visible(property, referer) || IsArgumentParameter(referer, property)) && !Skipped(property, referer)
                    && ReferenceEquals(Value(property, referer), refered))
                {
                    found.Add((referer, listed, -1, -1));
                }
            }
        }

        private bool IsInsideOwner(object referer)
        {
            if (owner == null)
            {
                return false;
            }

            var current = referer;
            for (var level = 0; current != null && level < 12; level++, current = usage.GetParent.Invoke(current, null))
            {
                if (ReferenceEquals(current, owner))
                {
                    return true;
                }
            }

            return false;
        }

        private bool Visible(object property, object holder)
        {
            try
            {
                return usage.PropertyVisible.Invoke(property, [holder]) is true;
            }
            catch
            {
                return false;
            }
        }

        private object? Value(object property, object holder)
        {
            try
            {
                return usage.PropertyValue.Invoke(property, [holder]);
            }
            catch
            {
                return null;
            }
        }

        private bool IsArgumentParameter(object holder, object property) =>
            usage.Argument.IsInstanceOfType(holder) && ReferenceEquals(property, usage.ArgumentParameter);

        /// <summary>
        /// Properties Find Usages doesn't report: a link's target node, and an argument's parameter or
        /// an event filter's attribute unless that is what's searched and it has no value.
        /// </summary>
        private bool Skipped(object property, object holder)
        {
            if (ReferenceEquals(property, usage.LinkTarget))
            {
                return true;
            }

            if (ReferenceEquals(property, usage.ArgumentParameter))
            {
                return !(Reflect.TryGet(holder, "Parameter") is { } parameter && ReferenceEquals(parameter, source) && Reflect.TryGet(holder, "Value") == null);
            }

            if (ReferenceEquals(property, usage.EventFilterAttribute))
            {
                return !(Reflect.TryGet(holder, "Attribute") is { } attribute && ReferenceEquals(attribute, source) && Reflect.TryGet(holder, "Value") == null);
            }

            return false;
        }

        private sealed class ResultComparer : IEqualityComparer<(object Owner, object Property, int Start, int Length)>
        {
            public bool Equals((object Owner, object Property, int Start, int Length) x, (object Owner, object Property, int Start, int Length) y) =>
                ReferenceEquals(x.Owner, y.Owner) && ReferenceEquals(x.Property, y.Property) && x.Start == y.Start && x.Length == y.Length;

            public int GetHashCode((object Owner, object Property, int Start, int Length) r) =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Owner), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Property), r.Start, r.Length);
        }
    }

    /// <summary>
    /// The public element with this key: a top-level element of the module, or one in its folders,
    /// as Service Studio's own key lookup finds it.
    /// </summary>
    private static object? FindReferenceable(ElementConsumersApi api, object parent, string elementId, int depth = 0)
    {
        foreach (var child in Reflect.Enumerate(api.Children.GetValue(parent)))
        {
            if (api.Referenceable.IsInstanceOfType(child) && KeyId(Reflect.TryGet(child, "Key")) == elementId)
            {
                return child;
            }

            // Folders hold elements; nothing nests deeper than a few folders.
            if (depth < 16 && api.Container.IsInstanceOfType(child) && FindReferenceable(api, child, elementId, depth + 1) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static ElementConsumersApi? elementConsumers;

    /// <summary>
    /// The public Service Studio members getElementConsumers uses: ESpace's read-only loader, the
    /// interfaces of a referenceable element and of what holds them, and the element overload of the
    /// server's consumer list.
    /// </summary>
    private static ElementConsumersApi ElementConsumersMembers()
    {
        if (elementConsumers != null)
        {
            return elementConsumers;
        }

        var eSpace = Reflect.FindType("ServiceStudio.Model.ESpace", "ServiceStudio.Framework");
        var load = eSpace.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == "LoadUnregisteredAndReadOnly" && m.GetParameters() is { Length: 7 } p && p[0].ParameterType == typeof(byte[]))
            ?? throw new BridgeException("ESpace.LoadUnregisteredAndReadOnly(byte[], …) is missing");
        var children = Reflect.FindType("ServiceStudio.Model.AbstractObject", "ServiceStudio.Framework").GetProperty("DirectChildren")
            ?? throw new BridgeException("AbstractObject.DirectChildren is missing");
        var getConsumers = Reflect.FindType("ServiceStudio.ServerCommunications.API.IApplicationManagementService", "ServiceStudio.ServerCommunications.API")
                .GetMethod("GetConsumers", [typeof(object)])
            ?? throw new BridgeException("IApplicationManagementService.GetConsumers(object) is missing");
        return elementConsumers = new ElementConsumersApi(
            load,
            Reflect.FindType("ServiceStudio.Model.Interfaces.ITopLevelReferenceableElement", "ServiceStudio.Framework"),
            Reflect.FindType("ServiceStudio.Model.Interfaces.ITopLevelReferenceableElementParent", "ServiceStudio.Framework"),
            children,
            getConsumers);
    }

    private static UsageApi? usageApi;

    /// <summary>The public Service Studio members the usage count reads (getElementConsumers with countUsages or part).</summary>
    private static UsageApi UsageMembers()
    {
        if (usageApi != null)
        {
            return usageApi;
        }

        const string Framework = "ServiceStudio.Framework";
        const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
        Type T(string name) => Reflect.FindType(name, Framework);
        PropertyInfo Property(Type type, string name, bool declaredOnly = false) =>
            type.GetProperty(name, Instance | (declaredOnly ? BindingFlags.DeclaredOnly : BindingFlags.Default))
            ?? throw new BridgeException($"{type.Name}.{name} is missing");
        MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, Instance, parameters) ?? throw new BridgeException($"{type.Name}.{name} is missing");
        object Descriptor(Type type, string name) =>
            type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? throw new BridgeException($"{type.Name}.{name} is missing");

        var abstractObject = T("ServiceStudio.Model.AbstractObject");
        var isBeingUsed = T("ServiceStudio.Model.ModelExtensions").GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == "IsBeingUsed" && m.GetParameters() is { Length: 3 } p && p.All(x => x.ParameterType == abstractObject))
            ?? throw new BridgeException("ModelExtensions.IsBeingUsed(AbstractObject, AbstractObject, AbstractObject) is missing");
        var modelObject = T("ServiceStudio.Model.Interfaces.IObject");
        var descriptor = T("ServiceStudio.Model.PropertyDescriptors.PropertyDescriptor");
        var typeRecordDefinition = T("ServiceStudio.Model.PropertyDescriptors.ITypeRecordDefinitionPropertyDescriptor");
        var propertyObject = T("ServiceStudio.Model.Interfaces.IPropertyObject");
        var anonymousAttribute = T("ServiceStudio.Model.AnonymousStructureAttribute");
        var argument = T("ServiceStudio.Model.Argument");
        var eventFilter = T("ServiceStudio.Model.EventFilter");
        var namedRecord = T("ServiceStudio.Model.AbstractNamedRecord");
        var entity = T("ServiceStudio.Model.AbstractEntity");
        var role = T("ServiceStudio.Model.AbstractRole");
        var key = T("ServiceStudio.Model.GlobalCompoundKey");
        var keyCreate = key.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [abstractObject])
            ?? throw new BridgeException("GlobalCompoundKey.Create(AbstractObject) is missing");
        return usageApi = new UsageApi(
            Property(abstractObject, "Referers"),
            Property(abstractObject, "IsUserVisible"),
            isBeingUsed,
            Method(abstractObject, "GetParent"),
            Property(abstractObject, "DirectChildren"),
            Property(abstractObject, "Properties"),
            Method(descriptor, "IsUserVisible", modelObject),
            Method(descriptor, "GetValue", modelObject),
            typeRecordDefinition,
            Property(typeRecordDefinition, "TypePropertyDescriptor"),
            propertyObject,
            Property(propertyObject, "AssociatedProperty"),
            Property(T("ServiceStudio.Model.PropertyDescriptors.IPropertyModelObjectPropertyDescriptor"), "AsPropertyDescriptor"),
            Method(propertyObject, "GetReferedObjectLocations", abstractObject),
            T("ServiceStudio.Model.ListType"),
            anonymousAttribute,
            Property(anonymousAttribute, "Parent", declaredOnly: true),
            Type.GetType($"ServiceStudio.Expressions.AbstractObjectExpression, {Framework}"),
            argument,
            Descriptor(argument, "ParameterPropertyDescriptor"),
            Descriptor(T("ServiceStudio.Model.AbstractLink"), "TargetNodePropertyDescriptor"),
            eventFilter,
            Descriptor(eventFilter, "AttributePropertyDescriptor"),
            [
                T("ServiceStudio.Model.Nodes+AbstractExecuteAction"),
                T("ServiceStudio.Model.ScriptNodes+ExecuteScriptAction"),
                T("ServiceStudio.Model.ProcessNodes+ExecuteProcess"),
                T("ServiceStudio.Model.Nodes+AbstractGoToDestinationNode"),
                T("ServiceStudio.Model.SimpleQueryRecord"),
            ],
            namedRecord,
            Method(namedRecord, "GetAttributes"),
            entity,
            Property(entity, "IdentifierType", declaredOnly: true),
            Property(entity, "IsStaticEntity"),
            Method(entity, "GetStaticRecords"),
            T("ServiceStudio.Model.AbstractElementActionSignature"),
            role,
            Property(role, "RoleException"),
            keyCreate,
            Method(key, "TryGetObject", T("ServiceStudio.Model.ESpace")));
    }
}
