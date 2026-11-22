using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Usage: ServiceStudioCheck <folder with ServiceStudioBridge.dll> <Service Studio's assembly folder>
// Service Studio's assemblies are only loaded for their types: none of their code runs.
var bridgeFolder = args[0];
var studioFolder = args[1];
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (var folder in new[] { bridgeFolder, studioFolder })
    {
        var path = Path.Combine(folder, name.Name + ".dll");
        if (File.Exists(path))
        {
            return context.LoadFromAssemblyPath(path);
        }
    }

    return null;
};

var bridge = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(bridgeFolder, "ServiceStudioBridge.dll"));
var status = bridge.GetType("ServiceStudioBridge.Studio", throwOnError: true)!
    .GetMethod("Status", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, null);
var capabilities = status!.GetType().GetProperty("Capabilities")!.GetValue(status)!;
Console.WriteLine(JsonSerializer.Serialize(capabilities, new JsonSerializerOptions { WriteIndented = true }));
var broken = capabilities.GetType().GetProperties().Where(p => p.GetValue(capabilities) is string s && !s.StartsWith("ok", StringComparison.Ordinal)).ToList();
var fallback = capabilities.GetType().GetProperties().Where(p => p.GetValue(capabilities) is string s && s.StartsWith("ok, through the fallback", StringComparison.Ordinal)).ToList();
foreach (var p in fallback)
{
    Console.WriteLine($"warn {p.Name} works only through its fallback in this Service Studio build: update the bridge's public route.");
}

return broken.Count == 0 ? 0 : 1;
