namespace ServiceStudioBridge;

/// <summary>
/// What the startup hook (Hook/StartupHook.cs) calls after loading this assembly. The hook itself
/// lives in a separate .NET Standard assembly, so processes on older runtimes never load this one.
/// </summary>
public static class Entry
{
    public static void Start() => Bridge.StartIfServiceStudio();
}
