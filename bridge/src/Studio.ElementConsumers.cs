using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// The modules that reference one public element: the list Service Studio's Find Usages in all
/// Modules asks the server for before it searches each module. The bridge stops at the list. The
/// server takes the element itself, not its key: Service Studio passes the element of the producer
/// it has open, and the bridge does the same, or loads the producer's published version in memory
/// when it isn't open. An extension has no such element (it has no OML), so it isn't covered.
/// </summary>
internal static partial class Studio
{
    private sealed record ElementConsumersApi(MethodInfo Load, Type Referenceable, Type Container, PropertyInfo Children, MethodInfo GetConsumers);

    public static object GetElementConsumers(string? host, string element, string? producer, string? kind, int? limit)
    {
        var connection = Pick(host);
        var matches = PublicElementsNamed(connection, element, producer, kind);
        if (matches.Count == 0)
        {
            throw new BridgeException($"No module exposes a public element named {element}"
                + (kind != null ? $" of kind {kind}" : string.Empty) + (producer != null ? $" in {producer}" : string.Empty)
                + ". searchPublicElements finds names that are close.");
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
        var service = ApplicationService(connection);
        var producerKey = Reflect.Get(match, "ApplicationKey") ?? throw new BridgeException("The public element has no producer key.");
        var elementId = KeyId(Reflect.Get(match, "Key")) ?? throw new BridgeException("The public element has no key.");
        var producerId = KeyId(producerKey);
        // The service's own name for the producer (ServiceCenter, not (System)), as Service Studio's tab shows it.
        var producerModule = Reflect.TryGet(match, "ApplicationName") as string ?? string.Empty;
        List<object> Consumers(object found) => Reflect.Enumerate(api.GetConsumers.Invoke(service, [found]));

        // As Service Studio does: the producer as open, when it is (any version holds the element's key).
        List<object>? consumers = null;
        var readFrom = $"{producerModule} as open in Service Studio";
        var tab = OpenTab(connection, producerModule);
        if (Reflect.TryGet(tab, "OwnerESpace") is { } open && KeyId(Reflect.TryGet(open, "Key")) == producerId)
        {
            consumers = Run(() =>
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

                return found == null ? null : Consumers(found);
            });
        }

        if (consumers == null)
        {
            readFrom = $"{producerModule}'s published version, loaded in memory";
            var (_, bytes) = Run(() => Download(connection, 0, producerKey), timeoutSeconds: 300);
            consumers = Run(() =>
            {
                var eSpace = api.Load.Invoke(null, [bytes, null, null, null, null, null, null])
                    ?? throw new BridgeException($"Service Studio loaded nothing from {producerModule}'s OML.");
                try
                {
                    var found = FindReferenceable(api, eSpace, elementId)
                        ?? throw new BridgeException($"{producerModule}'s published version has no {DescribeElement(match)}: it may be in a version not "
                            + "published yet. Ask the user to open the module in Service Studio, or publish it, then try again.");
                    return Consumers(found);
                }
                finally
                {
                    (eSpace as IDisposable)?.Dispose();
                }
            });
        }

        return Scoped(connection, new
        {
            Element = DescribeElement(match),
            ReadFrom = readFrom,
            Note = "The server's list, from the consumers' published versions: a module with unpublished changes to its references can be missing, "
                + "or listed when it no longer uses the element.",
            Consumers = Page(ConsumerRows(consumers, Modules(connection)), limit, null),
        });
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
}
