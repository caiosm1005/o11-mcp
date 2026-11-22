using System;
using System.IO;
using System.Reflection;

// The .NET runtime calls StartupHook.Initialize() in every process started with this assembly
// listed in DOTNET_STARTUP_HOOKS. The type must stay in the global namespace, and this assembly
// must load on any runtime: it references nothing beyond .NET Standard 2.0.
public static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SSBRIDGE_DISABLE") == "1" || !IsServiceStudio())
            {
                return;
            }

            var folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? string.Empty;
            var bridge = Assembly.LoadFrom(Path.Combine(folder, "ServiceStudioBridge.dll"));
            bridge.GetType("ServiceStudioBridge.Entry", throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }
        catch
        {
            // Nothing may escape: an exception here stops the host process from starting.
        }
    }

    // Only Service Studio's main process, not ODC Studio, its helper processes or any other .NET
    // app. Environment.ProcessPath arrived in .NET 6; a runtime without it isn't Service Studio's.
    private static bool IsServiceStudio()
    {
        var path = typeof(Environment).GetProperty("ProcessPath")?.GetValue(null) as string;
        return string.Equals(Path.GetFileNameWithoutExtension(path), "ServiceStudio", StringComparison.OrdinalIgnoreCase);
    }
}
