using System.Reflection;

namespace ServiceStudioBridge;

/// <summary>
/// Settings a module keeps on its references to other modules' entities. Service Studio's Model API
/// exposes them read-only on a reference (IEntitySignature), but Service Studio's own model object
/// for a referenced entity (ServiceStudio.Model.ReferenceEntity) takes them, as its properties
/// panel shows: the bridge sets them there, inside one of Service Studio's undoable commands.
/// </summary>
internal static partial class Studio
{
    private sealed record ReferenceEntityApi(PropertyInfo References, PropertyInfo Entities, PropertyInfo Hidden, PropertyInfo MultiTenant, PropertyInfo ShowTenantIdentifier, MethodInfo Execute);

    private static ReferenceEntityApi? referenceEntityApi;

    /// <summary>The public Service Studio members setReferenceTenantIdentifier reads and writes.</summary>
    private static ReferenceEntityApi ReferenceEntityMembers()
    {
        if (referenceEntityApi != null)
        {
            return referenceEntityApi;
        }

        PropertyInfo Property(string type, string name, bool write = false)
        {
            var property = Reflect.FindType(type, "ServiceStudio.Framework").GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property != null && property.CanRead && (!write || property.CanWrite)
                ? property
                : throw new BridgeException($"{type[(type.LastIndexOf('.') + 1)..]}.{name} is missing{(write ? " or read-only" : string.Empty)}");
        }

        var command = Reflect.FindType("ServiceStudio.Commands.Command", "ServiceStudio.Framework");
        return referenceEntityApi = new ReferenceEntityApi(
            Property("ServiceStudio.Model.ESpace", "References"),
            Property("ServiceStudio.Model.Reference", "ReferenceEntities"),
            Property("ServiceStudio.Model.ReferenceEntity", "Hidden"),
            Property("ServiceStudio.Model.ReferenceEntity", "EffectiveIsMultitenant"),
            Property("ServiceStudio.Model.ReferenceEntity", "ShowTenantIdentifier", write: true),
            command.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "Execute" && m.GetParameters() is { Length: 3 } p && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(System.Action)));
    }

    /// <summary>
    /// Sets Show Tenant Identifier on a module's reference to another module's entity, as its
    /// properties panel does: one undoable step, unsaved. Service Studio allows it only for a
    /// multi-tenant entity, and the setting belongs to the consumer: the producer is untouched.
    /// </summary>
    public static object SetReferenceTenantIdentifier(string? host, string module, string producer, string entity, bool value)
    {
        var (connection, tab, name) = ResolveOpenModule(host, module);
        if (tab == null)
        {
            throw NotOpen(module, connection);
        }

        module = name;
        var eSpace = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");

        // A change to the live module while Service Studio's MCP server has a working copy checked
        // out would be put back by its next omlMerge, which keeps the working copy's side.
        string? warning = null;
        switch (EditSessionOn(eSpace, out var sessionProblem))
        {
            case true:
                throw new BridgeException($"Service Studio's MCP server has an edit session (or unmerged changes) on {module}, and its next omlMerge would put "
                    + "Show Tenant Identifier back. Merge what the session holds (omlMerge), end it (endOmlEditSession), call this again, then start a new "
                    + "session: its snapshot includes the change.");
            case null:
                warning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}). If one is open, its next omlMerge puts the old value back.";
                break;
        }

        var api = ReferenceEntityMembers();
        object? target = null;
        bool? before = null;
        InContext(tab, $"Reading {module}'s reference to {entity}", () =>
        {
            var references = Reflect.Enumerate(api.References.GetValue(eSpace));
            var reference = references.FirstOrDefault(r => string.Equals(Reflect.TryGet(r, "Name") as string, producer, StringComparison.OrdinalIgnoreCase));
            if (reference == null)
            {
                var holders = references
                    .Where(r => Reflect.Enumerate(api.Entities.GetValue(r)).Any(e => string.Equals(Reflect.TryGet(e, "Name") as string, entity, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => Reflect.TryGet(r, "Name") as string)
                    .ToList();
                throw new BridgeException($"{module} doesn't reference {producer}."
                    + (holders.Count > 0 ? $" It references an entity named {entity} from: {string.Join(", ", holders)}." : string.Empty));
            }

            var entities = Reflect.Enumerate(api.Entities.GetValue(reference));
            target = entities.FirstOrDefault(e => string.Equals(Reflect.TryGet(e, "Name") as string, entity, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException($"{module} references no entity named {entity} from {producer}. It references: "
                    + string.Join(", ", entities.Select(e => Reflect.TryGet(e, "Name") as string).Where(n => n != null).Order(StringComparer.OrdinalIgnoreCase)) + ".");
            if (api.Hidden.GetValue(target) is true)
            {
                throw new BridgeException($"{module} holds {entity} only as a hidden stub (it came in as the type of something it uses), not as a dependency: "
                    + "add the dependency first (addDependency).");
            }

            if (api.MultiTenant.GetValue(target) is not true)
            {
                throw new BridgeException($"{entity} isn't a multi-tenant entity, so Service Studio doesn't let a consumer set Show Tenant Identifier on it.");
            }

            before = api.ShowTenantIdentifier.GetValue(target) is true;
        }, wait: true);

        if (before == value)
        {
            return Scoped(connection, new
            {
                Module = module,
                Reference = $"{entity} in {producer}",
                ShowTenantIdentifier = value,
                Status = "unchanged",
                Note = "Nothing changed: it was set that way already.",
                Warning = warning,
            });
        }

        bool? after = null;
        InContext(tab, $"Setting Show Tenant Identifier on {module}'s reference to {entity}", () =>
        {
            System.Action write = () => api.ShowTenantIdentifier.SetValue(target, value);
            api.Execute.Invoke(null, [Reflect.Call(tab, "GetPresenterContext"), "Show Tenant Identifier", write]);
            after = api.ShowTenantIdentifier.GetValue(target) is true;
        }, wait: true);
        if (after != value)
        {
            throw new BridgeException($"Service Studio took the change, but {entity}'s Show Tenant Identifier in {module} still reads {after}: set it in its properties.");
        }

        return Scoped(connection, new
        {
            Module = module,
            Reference = $"{entity} in {producer}",
            Before = before,
            After = after,
            Status = "changed",
            Note = "Unsaved in Service Studio, as one undoable step. Start an edit session after it: the session's snapshot includes it, and omlMerge keeps it.",
            Warning = warning,
        });
    }
}
