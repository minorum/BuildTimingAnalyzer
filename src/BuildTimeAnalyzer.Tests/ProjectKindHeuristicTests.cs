using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class ProjectKindHeuristicTests
{
    [Test]
    public async Task Classify_TestSuffix_ReturnsTest()
    {
        await Assert.That(ProjectKindHeuristic.Classify("MyApp.Tests")).IsEqualTo(ProjectKind.Test);
        await Assert.That(ProjectKindHeuristic.Classify("MyAppTests")).IsEqualTo(ProjectKind.Test);
        await Assert.That(ProjectKindHeuristic.Classify("MyAppTest")).IsEqualTo(ProjectKind.Test);
    }

    [Test]
    public async Task Classify_BenchmarkNames_ReturnsBenchmark()
    {
        await Assert.That(ProjectKindHeuristic.Classify("MyApp.Benchmarks")).IsEqualTo(ProjectKind.Benchmark);
        await Assert.That(ProjectKindHeuristic.Classify("MyApp.Bench")).IsEqualTo(ProjectKind.Benchmark);
        await Assert.That(ProjectKindHeuristic.Classify("PerfBenchmark")).IsEqualTo(ProjectKind.Benchmark);
    }

    [Test]
    public async Task Classify_OrdinaryName_ReturnsOther()
    {
        await Assert.That(ProjectKindHeuristic.Classify("MyApp.Core")).IsEqualTo(ProjectKind.Other);
        await Assert.That(ProjectKindHeuristic.Classify("Web")).IsEqualTo(ProjectKind.Other);
    }

    [Test]
    public async Task Classify_Empty_ReturnsOther()
    {
        await Assert.That(ProjectKindHeuristic.Classify("")).IsEqualTo(ProjectKind.Other);
    }

    [Test]
    public async Task Label_TagsHeuristicKinds()
    {
        await Assert.That(ProjectKindHeuristic.Label(ProjectKind.Test)).Contains("heuristic");
        await Assert.That(ProjectKindHeuristic.Label(ProjectKind.Benchmark)).Contains("heuristic");
        await Assert.That(ProjectKindHeuristic.Label(ProjectKind.Other)).IsEqualTo("other");
    }
}
