using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

public sealed class HtmlReportExporterTests
{
    private static BuildReport CreateSampleReport(bool succeeded = true, ProjectTiming? overrideProject = null)
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0);
        var project = overrideProject ?? new ProjectTiming
        {
            Name = "MyApp",
            FullPath = "C:\\src\\MyApp\\MyApp.csproj",
            SelfTime = TimeSpan.FromSeconds(12.5),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            SelfPercent = 62.5,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(12.5),
        };

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(20),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            AttributedWarningCount = 3,
            WarningsByCode = [],
            GeneratedComInterfaceUsages = [],
            Projects = [project],
            TopTargets = [],
            Context = new BuildContext { Configuration = "Release" },
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>(),
            ExecutedTargetCount = 10,
            SkippedTargetCount = 2,
            PotentiallyCustomTargets = [],
            ReferenceOverhead = null,
            SpanOutliers = [],
            ProjectCountTax = TestDefaults.EmptyTax(1),
            TopTasks = [],
            SkipReasons = [],
            AnalyzerReports = [],
            ProjectDiagnoses = [],
            Graph = TestDefaults.EmptyGraph(1),
            CriticalPath = [],
            CriticalPathTotal = TimeSpan.Zero,
            CriticalPathValidation = TestDefaults.EmptyValidation(),
        };
    }

    [Test]
    public async Task Export_ProducesValidHtmlFile()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("<!DOCTYPE html>");
            await Assert.That(html).Contains("</html>");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsProjectName()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("MyApp");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsTimeSummary()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            // New one-line summary uses "elapsed" and the Top Consumers section uses "Time".
            await Assert.That(html).Contains("elapsed");
            await Assert.That(html).Contains("Top time consumers");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsBuildContextWhenAvailable()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("Build Context");
            await Assert.That(html).Contains("Release");
            await Assert.That(html).Contains("2 of 12 targets skipped");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_SucceededBuild_ShowsSuccessInSummary()
    {
        var report = CreateSampleReport(succeeded: true);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            // One-line summary; no decorative badge.
            await Assert.That(html).Contains("Build Succeeded");
            await Assert.That(html).Contains("summary-line");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_FailedBuild_ShowsFailureInSummary()
    {
        var report = CreateSampleReport(succeeded: false);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("Build Failed");
            await Assert.That(html).Contains("summary-line");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_EscapesHtmlSpecialCharacters()
    {
        var project = new ProjectTiming
        {
            Name = "<script>alert('xss')</script>",
            FullPath = "C:\\src\\test.csproj",
            SelfTime = TimeSpan.FromSeconds(5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            SelfPercent = 100,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(5),
        };
        var report = CreateSampleReport(overrideProject: project);

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).DoesNotContain("<script>alert");
            await Assert.That(html).Contains("&lt;script&gt;");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_WithAnalysis_ContainsFindingsSection()
    {
        var report = CreateSampleReport();
        var analysis = new BuildAnalysis
        {
            Findings =
            [
                new AnalysisFinding
                {
                    Number = 1,
                    Title = "Test finding title",
                    Measured = "Test measured facts",
                    LikelyExplanation = "A possible heuristic reason",
                    InvestigationSuggestion = "Look into this",
                    Severity = FindingSeverity.Critical,
                    Confidence = FindingConfidence.High,
                    Evidence = "SelfTime=10s",
                    ThresholdName = "SomeThreshold=25%",
                },
            ],
            Recommendations =
            [
                new AnalysisRecommendation { Number = 1, Text = "Investigate the issue" },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, analysis);
            var html = File.ReadAllText(path);
            // Bottleneck card: title + measured + caveat (LikelyExplanation) + inspect line.
            // The standalone Recommendations section was removed per spec, but a finding's
            // LikelyExplanation is its load-bearing hedge and must survive into the HTML.
            await Assert.That(html).Contains("Test finding title");
            await Assert.That(html).Contains("Test measured facts");
            await Assert.That(html).Contains("A possible heuristic reason");
            await Assert.That(html).Contains("Look into this");
            await Assert.That(html).Contains("bottleneck");
            await Assert.That(html).Contains("sev-critical");
        }
        finally { File.Delete(path); }
    }

    // A project whose whole self time is a single category, for exercising the flamegraph.
    private static ProjectTiming FlameProject(string name, double seconds, TargetCategory category = TargetCategory.Compile) =>
        new()
        {
            Name = name,
            FullPath = $"C:\\src\\{name}\\proj.csproj",
            SelfTime = TimeSpan.FromSeconds(seconds),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            SelfPercent = 100,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(seconds),
            CategoryBreakdown = new Dictionary<TargetCategory, TimeSpan> { [category] = TimeSpan.FromSeconds(seconds) },
        };

    [Test]
    public async Task Export_ContainsFlamegraph()
    {
        // 30s work / 20s wall clock => 1.5× parallel (above the 1× threshold, so the metric shows).
        var report = CreateSampleReport(overrideProject: FlameProject("MyApp", 30)) with
        {
            TotalSelfTime = TimeSpan.FromSeconds(30),
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = TimeSpan.FromSeconds(30),
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("Where the Time Went");
            await Assert.That(html).Contains("id=\"ftflame\"");
            // Frames are rendered as static HTML — no embedded JSON data blob.
            await Assert.That(html).Contains("ft-frame");
            await Assert.That(html).DoesNotContain("application/json");
            // Category frame comes from the category set; the per-project child frame comes from the
            // project's CategoryBreakdown — assert both are actually wired and rendered.
            await Assert.That(html).Contains("Compiling code");
            await Assert.That(html).Contains("data-name=\"MyApp\"");
            // 30s work / 20s wall clock => 1.5× parallel (exact formatted value).
            await Assert.That(html).Contains("1.5×");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_FlamegraphEscapesProjectNames()
    {
        var report = CreateSampleReport(overrideProject: FlameProject("</script><img src=x>", 5)) with
        {
            TotalSelfTime = TimeSpan.FromSeconds(5),
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = TimeSpan.FromSeconds(5),
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            // The project name is a flamegraph frame label; it must be HTML-escaped there too.
            await Assert.That(html).DoesNotContain("<img src=x>");
            await Assert.That(html).Contains("&lt;/script&gt;&lt;img src=x&gt;");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_FlamegraphReconcilesRemaindersAndTags()
    {
        // 60s project under an 80s Compile category (=> 20s "shared build steps" remainder), and
        // 100s total work vs 80s categorized (=> 20s top-level "other build steps"). 100s/200s wall
        // clock => 0.5x parallel, below the 1x threshold.
        var report = CreateSampleReport(overrideProject: FlameProject("MyApp", 60)) with
        {
            EndTime = new DateTime(2025, 6, 15, 10, 3, 20), // start + 200s
            TotalSelfTime = TimeSpan.FromSeconds(100),
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = TimeSpan.FromSeconds(80),
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            // Category-level and top-level remainders both surface (nothing dropped).
            await Assert.That(html).Contains("shared build steps");
            await Assert.That(html).Contains("other build steps");
            // Synthetic frames carry their own description; real project frames are tagged.
            await Assert.That(html).Contains("data-desc=");
            await Assert.That(html).Contains("data-role=\"project\"");
            // The "work" chip mirrors the flamegraph root total (100s => "1m 40s"), one denominator.
            await Assert.That(html).Contains("1m 40s");
            // 0.5x parallel: both the parallel chip and the note's parallelism clause are suppressed.
            await Assert.That(html).DoesNotContain("parallel");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_FlamegraphDedupesSameNamedProjects()
    {
        // Two projects sharing a short name (different folders) carry the same merged breakdown in
        // production; the flamegraph must count that once, not render a doubled child frame.
        var a = FlameProject("Dup", 10) with { FullPath = "C:\\src\\one\\Dup.csproj" };
        var b = FlameProject("Dup", 10) with { FullPath = "C:\\src\\two\\Dup.csproj" };
        var report = CreateSampleReport() with
        {
            Projects = [a, b],
            TotalSelfTime = TimeSpan.FromSeconds(10),
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan> { [TargetCategory.Compile] = TimeSpan.FromSeconds(10) },
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path);
            var html = File.ReadAllText(path);
            // data-name is flamegraph-only; the deduped project must appear as exactly one frame.
            var frameCount = html.Split("data-name=\"Dup\"").Length - 1;
            await Assert.That(frameCount).IsEqualTo(1);
        }
        finally { File.Delete(path); }
    }
}
