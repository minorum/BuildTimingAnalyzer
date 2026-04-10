using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class DependencyGraphAnalyzerTests
{
    private static ProjectTiming P(string name) => new()
    {
        Name = name,
        FullPath = $"C:\\src\\{name}.csproj",
        SelfTime = TimeSpan.FromSeconds(1),
        Succeeded = true,
        ErrorCount = 0,
        WarningCount = 0,
        SelfPercent = 0,
        StartOffset = TimeSpan.Zero,
        EndOffset = TimeSpan.FromSeconds(1),
    };

    [Test]
    public async Task EmptyGraph_IsUsableForSingleProject()
    {
        var graph = DependencyGraphAnalyzer.Build([P("A")], new Dictionary<string, IReadOnlyList<string>>());

        await Assert.That(graph.IsUsable).IsTrue();
        await Assert.That(graph.Health.TotalProjects).IsEqualTo(1);
        await Assert.That(graph.Health.TotalEdges).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyGraph_NotUsableForMultipleProjects()
    {
        var graph = DependencyGraphAnalyzer.Build(
            [P("A"), P("B"), P("C")],
            new Dictionary<string, IReadOnlyList<string>>());

        await Assert.That(graph.IsUsable).IsFalse();
        await Assert.That(graph.Health.TotalEdges).IsEqualTo(0);
        await Assert.That(graph.Health.IsolatedNodes).IsEqualTo(3);
    }

    [Test]
    public async Task SimpleChain_CountsEdgesCorrectly()
    {
        var a = P("A");
        var b = P("B");
        var c = P("C");
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { b.FullPath },
            [b.FullPath] = new[] { c.FullPath },
        };

        var graph = DependencyGraphAnalyzer.Build([a, b, c], edges);

        await Assert.That(graph.IsUsable).IsTrue();
        await Assert.That(graph.Health.TotalEdges).IsEqualTo(2);
        await Assert.That(graph.LongestChainProjectCount).IsEqualTo(3);
        await Assert.That(graph.Health.IsolatedNodes).IsEqualTo(0);
    }

    [Test]
    public async Task FanIn_IdentifiesHub()
    {
        var hub = P("Hub");
        var a = P("A");
        var b = P("B");
        var c = P("C");
        // A, B, C all depend on Hub
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { hub.FullPath },
            [b.FullPath] = new[] { hub.FullPath },
            [c.FullPath] = new[] { hub.FullPath },
        };

        var graph = DependencyGraphAnalyzer.Build([a, b, c, hub], edges);

        var topHub = graph.TopHubs[0];
        await Assert.That(topHub.ProjectName).IsEqualTo("Hub");
        await Assert.That(topHub.IncomingCount).IsEqualTo(3);
    }

    [Test]
    public async Task Cycle_IsDetected()
    {
        var a = P("A");
        var b = P("B");
        var c = P("C");
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { b.FullPath },
            [b.FullPath] = new[] { c.FullPath },
            [c.FullPath] = new[] { a.FullPath },
        };

        var graph = DependencyGraphAnalyzer.Build([a, b, c], edges);

        await Assert.That(graph.Cycles.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SelfLoop_IsIgnored()
    {
        var a = P("A");
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { a.FullPath },
        };

        var graph = DependencyGraphAnalyzer.Build([a], edges);

        await Assert.That(graph.Health.TotalEdges).IsEqualTo(0);
    }
}
