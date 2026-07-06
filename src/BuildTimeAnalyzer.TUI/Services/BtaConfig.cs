using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Optional user configuration, loaded from a <c>btanalyzer.json</c> file discovered by walking up
/// from the analysed project's directory (or an explicit <c>--config</c> path). Lets a team extend
/// the "known heavy package" list and tune finding thresholds without recompiling. Missing or
/// malformed config silently degrades to <see cref="Default"/> — configuration is never fatal.
/// </summary>
public sealed record BtaConfig
{
    public const string DefaultFileName = "btanalyzer.json";

    /// <summary>Additional package ids to treat as "known heavy" (merged with the built-in set).</summary>
    public IReadOnlyList<string> HeavyPackages { get; init; } = [];

    public AnalyzerThresholds Thresholds { get; init; } = AnalyzerThresholds.Default;

    public static BtaConfig Default { get; } = new();

    /// <summary>
    /// Resolve and load config. An explicit path wins (returns <see cref="Default"/> if it does not
    /// exist); otherwise walks up from <paramref name="startDir"/> looking for <c>btanalyzer.json</c>.
    /// Any read/parse error degrades to <see cref="Default"/>.
    /// </summary>
    public static BtaConfig Load(string? explicitPath, string startDir)
    {
        var path = ResolvePath(explicitPath, startDir);
        if (path is null) return Default;
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>Parse config from a JSON string. Empty/partial objects fall back to defaults per field.</summary>
    public static BtaConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        var dto = JsonSerializer.Deserialize(json, BtaConfigJsonContext.Default.BtaConfigDto);
        return dto is null ? Default : FromDto(dto);
    }

    /// <summary>Locate the config file to use, or null if none applies.</summary>
    public static string? ResolvePath(string? explicitPath, string startDir)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        var dir = Directory.Exists(startDir) ? startDir : SafeDir(startDir);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, DefaultFileName);
            if (File.Exists(candidate)) return candidate;
            var parent = SafeDir(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static string? SafeDir(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private static BtaConfig FromDto(BtaConfigDto dto)
    {
        var t = AnalyzerThresholds.Default;
        if (dto.Thresholds is { } th)
        {
            t = new AnalyzerThresholds
            {
                LargestShareWarningPercent = th.LargestShareWarningPercent ?? t.LargestShareWarningPercent,
                LargestShareCriticalPercent = th.LargestShareCriticalPercent ?? t.LargestShareCriticalPercent,
                CostlyResolvePackageAssetsSeconds = th.CostlyResolvePackageAssetsSeconds ?? t.CostlyResolvePackageAssetsSeconds,
                TfmNegotiationAggregateSeconds = th.TfmNegotiationAggregateSeconds ?? t.TfmNegotiationAggregateSeconds,
                WarningsOnCriticalPathPerProject = th.WarningsOnCriticalPathPerProject ?? t.WarningsOnCriticalPathPerProject,
            };
        }

        IReadOnlyList<string> heavy = dto.HeavyPackages is null
            ? []
            : dto.HeavyPackages.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        return new BtaConfig { HeavyPackages = heavy, Thresholds = t };
    }
}

internal sealed class BtaConfigDto
{
    public List<string>? HeavyPackages { get; init; }
    public BtaThresholdsDto? Thresholds { get; init; }
}

internal sealed class BtaThresholdsDto
{
    public double? LargestShareWarningPercent { get; init; }
    public double? LargestShareCriticalPercent { get; init; }
    public double? CostlyResolvePackageAssetsSeconds { get; init; }
    public double? TfmNegotiationAggregateSeconds { get; init; }
    public int? WarningsOnCriticalPathPerProject { get; init; }
}

[JsonSerializable(typeof(BtaConfigDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class BtaConfigJsonContext : JsonSerializerContext;
