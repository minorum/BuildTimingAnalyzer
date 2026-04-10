using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class CriticalPathAnalyzerTests
{
    private static ProjectTiming P(string name, double seconds) => new()
    {
        Name = name,
        FullPath = $"C:\\src\\{name}.csproj",
        SelfTime = TimeSpan.FromSeconds(seconds),
        Succeeded = true,
        ErrorCount = 0,
        WarningCount = 0,
        SelfPercent = 0,
        StartOffset = TimeSpan.Zero,
        EndOffset = TimeSpan.FromSeconds(seconds),
    };

    [Test]
    public async Task EmptyProjects_ReturnsEmptyPath()
    {
        var (path, total) = CriticalPathAnalyzer.Compute(
            [],
            new Dictionary<string, IReadOnlyList<string>>(),
            TimeSpan.FromSeconds(10));

        await Assert.That(path.Count).IsEqualTo(0);
        await Assert.That(total).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task SingleProject_ReturnsThatProject()
    {
        var a = P("A", 5);
        var (path, total) = CriticalPathAnalyzer.Compute(
            [a],
            new Dictionary<string, IReadOnlyList<string>>(),
            TimeSpan.FromSeconds(10));

        await Assert.That(path.Count).IsEqualTo(1);
        await Assert.That(path[0].Name).IsEqualTo("A");
        await Assert.That(total).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Chain_ReturnsLongestPath()
    {
        // A depends on B (5s), B depends on C (10s). C → B → A should be the path.
        var a = P("A", 3);
        var b = P("B", 5);
        var c = P("C", 10);

        var deps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { b.FullPath },
            [b.FullPath] = new[] { c.FullPath },
        };

        var (path, total) = CriticalPathAnalyzer.Compute([a, b, c], deps, TimeSpan.FromSeconds(30));

        await Assert.That(path.Count).IsEqualTo(3);
        await Assert.That(path[0].Name).IsEqualTo("C");
        await Assert.That(path[1].Name).IsEqualTo("B");
        await Assert.That(path[2].Name).IsEqualTo("A");
        await Assert.That(total).IsEqualTo(TimeSpan.FromSeconds(18));
    }

    [Test]
    public async Task Diamond_PicksLongerBranch()
    {
        // A depends on B and C; B depends on D (fast); C depends on D (slow).
        // Actually better diamond: A → B, A → C, B → D, C → D. Path cost = D + max(B, C) + A.
        var a = P("A", 2);
        var b = P("B", 8);  // fast branch
        var c = P("C", 20); // slow branch
        var d = P("D", 5);

        var deps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { b.FullPath, c.FullPath },
            [b.FullPath] = new[] { d.FullPath },
            [c.FullPath] = new[] { d.FullPath },
        };

        var (path, total) = CriticalPathAnalyzer.Compute([a, b, c, d], deps, TimeSpan.FromSeconds(60));

        // Expected critical chain: D → C → A = 5 + 20 + 2 = 27s
        await Assert.That(total).IsEqualTo(TimeSpan.FromSeconds(27));
        await Assert.That(path.Select(p => p.Name).ToList()).Contains("C");
        await Assert.That(path.Select(p => p.Name).ToList()).Contains("A");
    }

    [Test]
    public async Task ValidationGate_RejectsPathLongerThanWallClock()
    {
        // Build a simple chain that totals 30s of self time, but wall clock was only 10s.
        // This indicates a broken dependency model — the feature must refuse to ship the result.
        var a = P("A", 10);
        var b = P("B", 20);

        var deps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [a.FullPath] = new[] { b.FullPath },
        };

        var (path, total) = CriticalPathAnalyzer.Compute([a, b], deps, TimeSpan.FromSeconds(10));

        // Rejected → empty result
        await Assert.That(path.Count).IsEqualTo(0);
        await Assert.That(total).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task NoDependencies_PicksSingleLongestProject()
    {
        var a = P("A", 5);
        var b = P("B", 12);
        var c = P("C", 3);

        var (path, total) = CriticalPathAnalyzer.Compute(
            [a, b, c],
            new Dictionary<string, IReadOnlyList<string>>(),
            TimeSpan.FromSeconds(15));

        await Assert.That(path.Count).IsEqualTo(1);
        await Assert.That(path[0].Name).IsEqualTo("B");
        await Assert.That(total).IsEqualTo(TimeSpan.FromSeconds(12));
    }
}
