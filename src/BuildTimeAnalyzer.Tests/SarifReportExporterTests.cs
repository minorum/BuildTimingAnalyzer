using System.Text.Json;
using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

public sealed class SarifReportExporterTests
{
    private static BuildAnalysis OneWarning() => new()
    {
        Findings =
        [
            new AnalysisFinding
            {
                Number = 1,
                Title = "MyApp dominates build time",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Measured = "MyApp: 12s (100% of total).",
                InvestigationSuggestion = "Inspect MyApp.",
                Evidence = "SelfPercent=100",
                ThresholdName = "top-project-share > 15%",
            },
        ],
        Recommendations = [],
    };

    [Test]
    public async Task Export_ProducesValidSarifShape()
    {
        var report = SampleReports.Minimal();
        var path = Path.Combine(Path.GetTempPath(), $"sarif-{Guid.NewGuid():N}.sarif");
        try
        {
            SarifReportExporter.Export(report, path, OneWarning());
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            await Assert.That(root.GetProperty("version").GetString()).IsEqualTo("2.1.0");
            await Assert.That(root.TryGetProperty("$schema", out _)).IsTrue();

            var run = root.GetProperty("runs")[0];
            await Assert.That(run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString())
                .IsEqualTo("btanalyzer");

            var result = run.GetProperty("results")[0];
            await Assert.That(result.GetProperty("ruleId").GetString()).IsEqualTo("BTA0001");
            await Assert.That(result.GetProperty("level").GetString()).IsEqualTo("warning");
            await Assert.That(result.GetProperty("message").GetProperty("text").GetString()!)
                .Contains("Inspect MyApp.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Export_NoFindings_EmptyResults()
    {
        var report = SampleReports.Minimal();
        var path = Path.Combine(Path.GetTempPath(), $"sarif-{Guid.NewGuid():N}.sarif");
        try
        {
            SarifReportExporter.Export(report, path, new BuildAnalysis { Findings = [], Recommendations = [] });
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
            await Assert.That(results.GetArrayLength()).IsEqualTo(0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Export_SeverityMapsToSarifLevel()
    {
        var analysis = new BuildAnalysis
        {
            Findings =
            [
                new AnalysisFinding
                {
                    Number = 1, Title = "Critical thing", Severity = FindingSeverity.Critical,
                    Confidence = FindingConfidence.High, Measured = "m", InvestigationSuggestion = "i",
                    Evidence = "e", ThresholdName = "t",
                },
            ],
            Recommendations = [],
        };
        var path = Path.Combine(Path.GetTempPath(), $"sarif-{Guid.NewGuid():N}.sarif");
        try
        {
            SarifReportExporter.Export(SampleReports.Minimal(), path, analysis);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
            await Assert.That(result.GetProperty("level").GetString()).IsEqualTo("error");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
