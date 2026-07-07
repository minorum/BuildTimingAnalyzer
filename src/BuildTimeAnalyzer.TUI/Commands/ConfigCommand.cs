using System.Globalization;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Commands;

/// <summary>
/// <c>btanalyzer init</c> writes a documented default <c>btanalyzer.json</c>; <c>btanalyzer config</c>
/// prints the effective configuration discovered from the current directory. Together they make the
/// config surface discoverable rather than something users must reverse-engineer from the README.
/// </summary>
public static class ConfigCommand
{
    public static Task<int> RunInitAsync(string[] args)
    {
        string? target = null;
        foreach (var a in args)
        {
            if (a is "-h" or "--help") { PrintInitHelp(); return Task.FromResult(0); }
            if (a.StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {a}"); return Task.FromResult(1); }
            target ??= a;
        }

        var path = Path.GetFullPath(target ?? BtaConfig.DefaultFileName);
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"Refusing to overwrite existing file: {path}");
            return Task.FromResult(1);
        }

        try
        {
            File.WriteAllText(path, BuildDefaultConfigJson());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write {path}: {ex.Message}");
            return Task.FromResult(1);
        }

        Console.WriteLine($"Wrote default config to: {path}");
        return Task.FromResult(0);
    }

    public static Task<int> RunPrintAsync(string[] args)
    {
        string? explicitPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintConfigHelp();
                    return Task.FromResult(0);
                case "--config":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --config"); return Task.FromResult(1); }
                    explicitPath = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return Task.FromResult(1);
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var resolved = BtaConfig.ResolvePath(explicitPath, cwd);
        var config = BtaConfig.Load(explicitPath, cwd);
        var t = config.Thresholds;

        Console.WriteLine(resolved is null
            ? "No btanalyzer.json found — using built-in defaults."
            : $"Config: {resolved}");
        Console.WriteLine("Effective thresholds:");
        Console.WriteLine($"  largestShareWarningPercent          = {Inv(t.LargestShareWarningPercent)}");
        Console.WriteLine($"  largestShareCriticalPercent         = {Inv(t.LargestShareCriticalPercent)}");
        Console.WriteLine($"  costlyResolvePackageAssetsSeconds   = {Inv(t.CostlyResolvePackageAssetsSeconds)}");
        Console.WriteLine($"  tfmNegotiationAggregateSeconds      = {Inv(t.TfmNegotiationAggregateSeconds)}");
        Console.WriteLine($"  warningsOnCriticalPathPerProject    = {t.WarningsOnCriticalPathPerProject}");
        Console.WriteLine($"  serializedBuildParallelismRatio     = {Inv(t.SerializedBuildParallelismRatio)}");
        Console.WriteLine($"  serializedBuildMinProjects          = {t.SerializedBuildMinProjects}");
        Console.WriteLine($"  projectCountTaxMinProjects          = {t.ProjectCountTaxMinProjects}");
        Console.WriteLine($"  projectCountTaxProjectSharePercent  = {Inv(t.ProjectCountTaxProjectSharePercent)}");
        Console.WriteLine($"Heavy packages (added): {(config.HeavyPackages.Count == 0 ? "(none)" : string.Join(", ", config.HeavyPackages))}");

        foreach (var w in config.Warnings)
            Console.Error.WriteLine($"warning: {w}");

        return Task.FromResult(0);
    }

    /// <summary>The default config template — kept in sync with <see cref="AnalyzerThresholds.Default"/>.</summary>
    public static string BuildDefaultConfigJson()
    {
        var t = AnalyzerThresholds.Default;
        return $$"""
        {
          "heavyPackages": [],
          "thresholds": {
            "largestShareWarningPercent": {{Inv(t.LargestShareWarningPercent)}},
            "largestShareCriticalPercent": {{Inv(t.LargestShareCriticalPercent)}},
            "costlyResolvePackageAssetsSeconds": {{Inv(t.CostlyResolvePackageAssetsSeconds)}},
            "tfmNegotiationAggregateSeconds": {{Inv(t.TfmNegotiationAggregateSeconds)}},
            "warningsOnCriticalPathPerProject": {{t.WarningsOnCriticalPathPerProject}},
            "serializedBuildParallelismRatio": {{Inv(t.SerializedBuildParallelismRatio)}},
            "serializedBuildMinProjects": {{t.SerializedBuildMinProjects}},
            "projectCountTaxMinProjects": {{t.ProjectCountTaxMinProjects}},
            "projectCountTaxProjectSharePercent": {{Inv(t.ProjectCountTaxProjectSharePercent)}}
          }
        }

        """;
    }

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static void PrintInitHelp()
    {
        Console.WriteLine("USAGE:");
        Console.WriteLine("    btanalyzer init [path]");
        Console.WriteLine();
        Console.WriteLine("Writes a btanalyzer.json with all thresholds at their defaults.");
        Console.WriteLine("Default path: ./btanalyzer.json. Refuses to overwrite an existing file.");
    }

    private static void PrintConfigHelp()
    {
        Console.WriteLine("USAGE:");
        Console.WriteLine("    btanalyzer config [--config <path>]");
        Console.WriteLine();
        Console.WriteLine("Prints the effective configuration (discovered near the current directory,");
        Console.WriteLine("or from --config), including any unknown-key warnings.");
    }
}
