using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class BuildComparisonTests
{
    private static BuildSnapshot Snapshot(long wallMs, long selfMs, params (string Name, long Ms)[] projects) => new()
    {
        WallClockMs = wallMs,
        TotalSelfTimeMs = selfMs,
        WarningCount = 0,
        ErrorCount = 0,
        Succeeded = true,
        Projects = projects.Select(p => new SnapshotProject { Name = p.Name, SelfTimeMs = p.Ms }).ToList(),
    };

    [Test]
    public async Task Compare_ComputesTopLevelDeltas()
    {
        var baseline = Snapshot(1000, 800);
        var current = Snapshot(1200, 900);

        var result = BuildComparison.Compare(baseline, current);

        await Assert.That(result.WallClockDeltaMs).IsEqualTo(200L);
        await Assert.That(Math.Round(result.WallClockDeltaPercent, 1)).IsEqualTo(20.0);
        await Assert.That(result.TotalSelfTimeDeltaMs).IsEqualTo(100L);
    }

    [Test]
    public async Task Compare_ClassifiesRegressionsAndImprovements()
    {
        var baseline = Snapshot(1000, 1000, ("A", 500), ("B", 500));
        var current = Snapshot(1000, 1000, ("A", 700), ("B", 300));

        var result = BuildComparison.Compare(baseline, current);

        await Assert.That(result.Regressions.Count).IsEqualTo(1);
        await Assert.That(result.Regressions[0].Name).IsEqualTo("A");
        await Assert.That(result.Regressions[0].DeltaMs).IsEqualTo(200L);
        await Assert.That(result.Improvements.Count).IsEqualTo(1);
        await Assert.That(result.Improvements[0].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Compare_DetectsAddedAndRemovedProjects()
    {
        var baseline = Snapshot(1000, 1000, ("A", 500), ("Old", 500));
        var current = Snapshot(1000, 1000, ("A", 500), ("New", 500));

        var result = BuildComparison.Compare(baseline, current);

        await Assert.That(result.AddedProjects.Contains("New")).IsTrue();
        await Assert.That(result.RemovedProjects.Contains("Old")).IsTrue();
    }

    [Test]
    public async Task Compare_DisambiguatesAddedProjectsWithCollidingNames()
    {
        var baseline = new BuildSnapshot { Projects = [] };
        var current = new BuildSnapshot
        {
            Projects =
            [
                new SnapshotProject { Name = "Common", SelfTimeMs = 100, FullPath = "/a/Common.csproj" },
                new SnapshotProject { Name = "Common", SelfTimeMs = 200, FullPath = "/b/Common.csproj" },
            ],
        };

        var result = BuildComparison.Compare(baseline, current);

        await Assert.That(result.AddedProjects.Count).IsEqualTo(2);
        await Assert.That(result.AddedProjects.Contains("Common (a)")).IsTrue();
        await Assert.That(result.AddedProjects.Contains("Common (b)")).IsTrue();
    }

    [Test]
    public async Task WorstRegressionPercent_TakesTheLarger()
    {
        var baseline = Snapshot(1000, 1000);
        var current = Snapshot(1100, 1300); // wall +10%, self +30%

        var result = BuildComparison.Compare(baseline, current);

        await Assert.That(Math.Round(result.WorstRegressionPercent, 1)).IsEqualTo(30.0);
    }

    [Test]
    public async Task Compare_DisambiguatesProjectsByFullPath()
    {
        // Two projects share the short name "Common" but live in different folders. Keyed by name
        // they would collapse; keyed by full path they stay distinct.
        var baseline = new BuildSnapshot
        {
            Projects =
            [
                new SnapshotProject { Name = "Common", SelfTimeMs = 100, FullPath = "/a/Common.csproj" },
                new SnapshotProject { Name = "Common", SelfTimeMs = 100, FullPath = "/b/Common.csproj" },
            ],
        };
        var current = new BuildSnapshot
        {
            Projects =
            [
                new SnapshotProject { Name = "Common", SelfTimeMs = 150, FullPath = "/a/Common.csproj" },  // +50
                new SnapshotProject { Name = "Common", SelfTimeMs = 900, FullPath = "/b/Common.csproj" },  // +800
            ],
        };

        var result = BuildComparison.Compare(baseline, current);

        // Both regressed and are reported separately. Keyed by name they would collapse to a single
        // entry (and drop the +800 regression entirely).
        await Assert.That(result.Regressions.Count).IsEqualTo(2);
        await Assert.That(result.Regressions[0].DeltaMs).IsEqualTo(800L);
    }

    [Test]
    public async Task ReadFromJsonReport_RoundTripsExportedReport()
    {
        var report = SampleReports.Minimal(wallSeconds: 20, projectName: "MyApp", projectSelfSeconds: 12);
        var path = Path.Combine(Path.GetTempPath(), $"cmp-{Guid.NewGuid():N}.json");
        try
        {
            BuildTimeAnalyzer.Export.JsonReportExporter.Export(report, path);
            var snapshot = BuildSnapshot.ReadFromJsonReport(path);

            await Assert.That(snapshot).IsNotNull();
            await Assert.That(snapshot!.Projects.Count).IsEqualTo(1);
            await Assert.That(snapshot.Projects[0].Name).IsEqualTo("MyApp");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadFromJsonReport_MissingFile_ReturnsNull()
    {
        var snapshot = BuildSnapshot.ReadFromJsonReport(Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
        await Assert.That(snapshot).IsNull();
    }
}
