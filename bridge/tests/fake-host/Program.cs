// Stays alive until killed, so the smoke test can talk to the hook it loaded.
var bridgeLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ServiceStudioBridge");
Console.WriteLine($"fake host {Environment.ProcessPath} running on .NET {Environment.Version}; bridge loaded: {bridgeLoaded}");
Thread.Sleep(Timeout.Infinite);
