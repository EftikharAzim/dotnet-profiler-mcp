using System.Diagnostics;

// Profiling target for tests and manual runs.
// Usage: BurnerApp [seconds]  (default 30)
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 30;

Console.WriteLine($"BurnerApp pid={Environment.ProcessId} burning CPU for {seconds}s...");
Burner.BurnCpu(TimeSpan.FromSeconds(seconds));
Console.WriteLine("BurnerApp done.");

internal static class Burner
{
    /// <summary>The method the CPU profile must attribute the samples to.</summary>
    public static void BurnCpu(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        double sink = 0;
        while (sw.Elapsed < duration)
        {
            for (int i = 1; i < 10_000; i++)
            {
                sink += Math.Sqrt(i) * Math.Sin(i);
            }
        }
        // Keep the JIT from eliding the loop.
        if (double.IsNaN(sink)) Console.WriteLine(sink);
    }
}
