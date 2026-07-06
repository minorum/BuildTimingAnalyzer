using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class MarkdownReportExporterTests
{
    [Test]
    public async Task BuildMarkdown_ContainsHeaderAndSummary()
    {
        var report = SampleReports.Minimal(wallSeconds: 20, projectName: "MyApp", warningCount: 3);
        var md = MarkdownReportExporter.BuildMarkdown(report, analysis: null);

        await Assert.That(md).Contains("# Build Timing Report");
        await Assert.That(md).Contains("MyApp");
        await Assert.That(md).Contains("Build OK");
        await Assert.That(md).Contains("3 warning(s)");
    }

    [Test]
    public async Task BuildMarkdown_NoFindings_ShowsPlaceholder()
    {
        var report = SampleReports.Minimal();
        var analysis = new BuildAnalysis { Findings = [], Recommendations = [] };
        var md = MarkdownReportExporter.BuildMarkdown(report, analysis);

        await Assert.That(md).Contains("_No findings._");
    }

    [Test]
    public async Task BuildMarkdown_RendersFindings()
    {
        var report = SampleReports.Minimal();
        var analysis = new BuildAnalysis
        {
            Findings =
            [
                new AnalysisFinding
                {
                    Number = 1,
                    Title = "MyApp dominates build time",
                    Severity = FindingSeverity.Critical,
                    Confidence = FindingConfidence.High,
                    Measured = "MyApp: 12s (100% of total).",
                    InvestigationSuggestion = "Inspect MyApp.",
                    Evidence = "SelfPercent=100",
                    ThresholdName = "top-project-share > 15%",
                },
            ],
            Recommendations = [],
        };
        var md = MarkdownReportExporter.BuildMarkdown(report, analysis);

        await Assert.That(md).Contains("[CRITICAL] MyApp dominates build time");
        await Assert.That(md).Contains("**Investigate:** Inspect MyApp.");
    }

    [Test]
    public async Task BuildMarkdown_IncludesComparisonWhenSupplied()
    {
        var report = SampleReports.Minimal();
        var baseline = new BuildSnapshot { WallClockMs = 1000, TotalSelfTimeMs = 1000 };
        var current = new BuildSnapshot { WallClockMs = 1200, TotalSelfTimeMs = 1100 };
        var comparison = BuildComparison.Compare(baseline, current);

        var md = MarkdownReportExporter.BuildMarkdown(report, analysis: null, comparison);

        await Assert.That(md).Contains("## Comparison vs baseline");
        await Assert.That(md).Contains("Wall-clock");
    }

    [Test]
    public async Task Export_WritesFile()
    {
        var report = SampleReports.Minimal();
        var path = Path.Combine(Path.GetTempPath(), $"md-{Guid.NewGuid():N}.md");
        try
        {
            MarkdownReportExporter.Export(report, path, analysis: null);
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(File.ReadAllText(path)).Contains("# Build Timing Report");
        }
        finally { File.Delete(path); }
    }
}
