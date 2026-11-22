using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ServiceStudioBridge;

internal static class Bridge
{
    public const int DefaultPort = 48720;

    public static string Version => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Starts the bridge on a background thread when the host process is Service Studio; does nothing otherwise.</summary>
    public static void StartIfServiceStudio()
    {
        if (Environment.GetEnvironmentVariable("SSBRIDGE_DISABLE") == "1")
        {
            return;
        }

        // DOTNET_STARTUP_HOOKS reaches every .NET process the user starts (ODC Studio, Service
        // Studio's own helper processes, …). Only Service Studio's main process runs the bridge.
        var process = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        if (!string.Equals(process, "ServiceStudio", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "ServiceStudioBridge" }.Start();
    }

    private static void Run()
    {
        try
        {
            var config = Config.Load();
            Log.Info($"Starting {Version} in {Environment.ProcessPath} (pid {Environment.ProcessId}) on port {config.Port}");
            new McpServer(config.Port, Paths.EnsureToken()).Run();
        }
        catch (System.Net.HttpListenerException ex) when (ex.ErrorCode is 48 or 98 or 32 or 183)
        {
            // Service Studio briefly starts helper processes with its own executable, which load
            // the hook too: the first process keeps the port and the others step aside.
            Log.Info($"Port {Config.Load().Port} is already taken (by another Service Studio process?); pid {Environment.ProcessId} won't serve the bridge.");
        }
        catch (Exception ex)
        {
            Log.Error("The bridge stopped", ex);
        }
    }
}

/// <summary>An error whose message is meant for the agent as is.</summary>
internal sealed class BridgeException(string message) : Exception(message);

internal static class Paths
{
    public static string Home
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SSBRIDGE_HOME");
            if (!string.IsNullOrEmpty(overridden))
            {
                return overridden;
            }

            return OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ServiceStudioBridge")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServiceStudioBridge");
        }
    }

    public static string ConfigFile => Path.Combine(Home, "config.json");

    public static string TokenFile => Path.Combine(Home, "token");

    public static string LogFile => Path.Combine(Home, "bridge.log");

    public static string Downloads => Path.Combine(Home, "downloads");

    /// <summary>Returns the bearer token clients must send, creating it (readable by the user only) on first use.</summary>
    public static string EnsureToken()
    {
        Directory.CreateDirectory(Home);
        if (File.Exists(TokenFile))
        {
            var existing = File.ReadAllText(TokenFile).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenFile, token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return token;
    }
}

/// <summary>config.json, optional: the port, read at start. The environment is whichever one Service Studio is signed in to.</summary>
internal sealed record Config(int Port)
{
    public static Config Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var node = JsonNode.Parse(File.ReadAllText(Paths.ConfigFile));
                return new Config(node?["port"]?.GetValue<int>() ?? Bridge.DefaultPort);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Can't read {Paths.ConfigFile}; using the defaults", ex);
        }

        return new Config(Bridge.DefaultPort);
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Home);
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                {
                    File.Move(file.FullName, file.FullName + ".1", overwrite: true);
                }

                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging never breaks the bridge.
        }
    }
}
