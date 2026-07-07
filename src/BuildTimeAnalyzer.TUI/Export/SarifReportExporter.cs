using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Export;

/// <summary>
/// Emits findings as SARIF 2.1.0 so they can be uploaded to GitHub code scanning and surface as
/// inline PR annotations. Findings are solution-level rather than line-level, so each result is
/// anchored to the analysed project/solution file. AOT-safe via source-generated serialization.
/// </summary>
public static class SarifReportExporter
{
    private const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string ToolUri = "https://github.com/minorum/BuildTimingAnalyzer";

    public static void Export(BuildReport report, string outputPath, BuildAnalysis? analysis = null)
    {
        var sarif = BuildSarif(report, analysis);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(sarif, SarifJsonContext.Default.SarifLog), System.Text.Encoding.UTF8);
    }

    internal static SarifLog BuildSarif(BuildReport report, BuildAnalysis? analysis)
    {
        var findings = analysis?.Findings ?? [];
        var artifactUri = ToUri(report.ProjectOrSolutionPath);

        var rules = new List<SarifRule>();
        var results = new List<SarifResult>();
        foreach (var f in findings)
        {
            var ruleId = $"BTA{f.Number:D4}";
            rules.Add(new SarifRule
            {
                Id = ruleId,
                Name = Slug(f.Title),
                ShortDescription = new SarifText { Text = f.Title },
                DefaultConfiguration = new SarifConfig { Level = Level(f.Severity) },
            });

            var text = f.LikelyExplanation is { Length: > 0 } expl
                ? $"{f.Measured} {expl} Investigate: {f.InvestigationSuggestion}"
                : $"{f.Measured} Investigate: {f.InvestigationSuggestion}";

            results.Add(new SarifResult
            {
                RuleId = ruleId,
                Level = Level(f.Severity),
                Message = new SarifText { Text = text },
                Locations =
                [
                    new SarifLocation
                    {
                        PhysicalLocation = new SarifPhysicalLocation
                        {
                            ArtifactLocation = new SarifArtifactLocation { Uri = artifactUri },
                        },
                    },
                ],
                Properties = new SarifResultProperties
                {
                    Confidence = f.Confidence.ToString(),
                    Threshold = f.ThresholdName,
                    UpperBoundImpactPercent = f.UpperBoundImpactPercent,
                },
            });
        }

        return new SarifLog
        {
            Schema = SchemaUri,
            Version = "2.1.0",
            Runs =
            [
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifDriver
                        {
                            Name = "btanalyzer",
                            InformationUri = ToolUri,
                            Version = BuildTimeAnalyzer.VersionInfo.Version,
                            Rules = rules,
                        },
                    },
                    Results = results,
                },
            ],
        };
    }

    private static string Level(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => "error",
        FindingSeverity.Warning => "warning",
        _ => "note",
    };

    // SARIF artifact URIs are relative; use the file name so code scanning can match it in-repo.
    private static string ToUri(string path)
    {
        try { return Path.GetFileName(path) is { Length: > 0 } name ? name : path; }
        catch { return path; }
    }

    private static string Slug(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-') is { Length: > 0 } s ? s : "finding";
    }
}

internal sealed class SarifLog
{
    [JsonPropertyName("$schema")] public required string Schema { get; init; }
    public required string Version { get; init; }
    public required List<SarifRun> Runs { get; init; }
}

internal sealed class SarifRun
{
    public required SarifTool Tool { get; init; }
    public required List<SarifResult> Results { get; init; }
}

internal sealed class SarifTool
{
    public required SarifDriver Driver { get; init; }
}

internal sealed class SarifDriver
{
    public required string Name { get; init; }
    public string? InformationUri { get; init; }
    public string? Version { get; init; }
    public required List<SarifRule> Rules { get; init; }
}

internal sealed class SarifRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SarifText ShortDescription { get; init; }
    public required SarifConfig DefaultConfiguration { get; init; }
}

internal sealed class SarifConfig
{
    public required string Level { get; init; }
}

internal sealed class SarifResult
{
    public required string RuleId { get; init; }
    public required string Level { get; init; }
    public required SarifText Message { get; init; }
    public required List<SarifLocation> Locations { get; init; }
    public SarifResultProperties? Properties { get; init; }
}

internal sealed class SarifLocation
{
    public required SarifPhysicalLocation PhysicalLocation { get; init; }
}

internal sealed class SarifPhysicalLocation
{
    public required SarifArtifactLocation ArtifactLocation { get; init; }
}

internal sealed class SarifArtifactLocation
{
    public required string Uri { get; init; }
}

internal sealed class SarifText
{
    public required string Text { get; init; }
}

internal sealed class SarifResultProperties
{
    public string? Confidence { get; init; }
    public string? Threshold { get; init; }
    public double? UpperBoundImpactPercent { get; init; }
}

[JsonSerializable(typeof(SarifLog))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SarifJsonContext : JsonSerializerContext;
