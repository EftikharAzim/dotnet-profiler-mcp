using System.Diagnostics;

namespace DotnetProfiler.Core.Tests;

internal static class TestEnvironment
{
    /// <summary>Absolute path to the dotnet host (not necessarily on PATH in this environment).</summary>
    public static string DotnetPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (root is not null && File.Exists(Path.Combine(root, "dotnet")))
                return Path.Combine(root, "dotnet");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userLocal = Path.Combine(home, ".dotnet", "dotnet");
            if (File.Exists(userLocal))
                return userLocal;

            return "dotnet"; // fall back to PATH
        }
    }

    /// <summary>Repo root, located by walking up from the test assembly to the .sln.</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotnetProfilerMcp.sln")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }

    public static string BurnerAppDll =>
        Path.Combine(RepoRoot, "samples", "BurnerApp", "bin", "Debug", "net9.0", "BurnerApp.dll");

    /// <summary>Starts BurnerApp for the given burn duration and waits until its runtime is attachable.</summary>
    public static Process StartBurnerApp(int burnSeconds)
    {
        if (!File.Exists(BurnerAppDll))
            throw new InvalidOperationException($"BurnerApp not built at {BurnerAppDll}; run dotnet build first.");

        var psi = new ProcessStartInfo(DotnetPath, $"\"{BurnerAppDll}\" {burnSeconds}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start BurnerApp.");

        // The diagnostic IPC endpoint appears shortly after runtime startup.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient
                .GetPublishedProcesses().Contains(process.Id))
                return process;
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"BurnerApp exited immediately: {process.StandardError.ReadToEnd()}");
            Thread.Sleep(100);
        }
        try { process.Kill(entireProcessTree: true); } catch { }
        throw new TimeoutException("BurnerApp never exposed a diagnostic endpoint.");
    }
}
