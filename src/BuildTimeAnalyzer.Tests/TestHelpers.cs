using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

/// <summary>Shared defaults for test BuildReport construction so tests only specify what they care about.</summary>
internal static class TestDefaults
{
    public static DependencyGraph EmptyGraph(int totalProjects = 0) => new()
    {
        Health = new DependencyGraphHealth
        {
            TotalProjects = totalProjects,
            TotalEdges = 0,
            IsolatedNodes = totalProjects,
            NodesWithOutgoing = 0,
            NodesWithIncoming = 0,
        },
        Nodes = [],
        TopHubs = [],
        LongestChainProjectCount = totalProjects > 0 ? 1 : 0,
        Cycles = [],
        CycleDetectionRan = true,
        IsUsable = totalProjects <= 1,
    };

    public static ProjectCountTaxStats EmptyTax(int totalProjects = 0) => new()
    {
        ReferencesExceedCompileCount = 0,
        ReferencesMajorityCount = 0,
        TinySelfHugeSpanCount = 0,
        TotalProjects = totalProjects,
        PerKindStats = [],
    };

    public static CriticalPathValidation EmptyValidation() => new()
    {
        ComputedTotal = TimeSpan.Zero,
        WallClock = TimeSpan.Zero,
        Accepted = false,
        Reason = "test default",
        GraphWasUsable = false,
    };
}
