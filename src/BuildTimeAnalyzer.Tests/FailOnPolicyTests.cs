using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class FailOnPolicyTests
{
    private static BuildAnalysis Analysis(params FindingSeverity[] severities) => new()
    {
        Findings = severities.Select((s, i) => new AnalysisFinding
        {
            Number = i + 1,
            Title = $"Finding {i + 1}",
            Severity = s,
            Confidence = FindingConfidence.High,
            Measured = "measured",
            InvestigationSuggestion = "investigate",
            Evidence = "evidence",
            ThresholdName = "threshold",
        }).ToList(),
        Recommendations = [],
    };

    [Test]
    public async Task EmptySpec_NeverTrips()
    {
        var result = FailOnPolicy.Evaluate(null, SampleReports.Minimal(), Analysis(), null);
        await Assert.That(result.Tripped).IsFalse();
    }

    [Test]
    public async Task Critical_TripsOnCriticalFinding()
    {
        var result = FailOnPolicy.Evaluate("critical", SampleReports.Minimal(), Analysis(FindingSeverity.Critical), null);
        await Assert.That(result.Tripped).IsTrue();
    }

    [Test]
    public async Task Critical_DoesNotTripOnWarningOnly()
    {
        var result = FailOnPolicy.Evaluate("critical", SampleReports.Minimal(), Analysis(FindingSeverity.Warning), null);
        await Assert.That(result.Tripped).IsFalse();
    }

    [Test]
    public async Task Wallclock_TripsWhenExceeded()
    {
        var report = SampleReports.Minimal(wallSeconds: 20);
        await Assert.That(FailOnPolicy.Evaluate("wallclock:10", report, Analysis(), null).Tripped).IsTrue();
        await Assert.That(FailOnPolicy.Evaluate("wallclock:30", report, Analysis(), null).Tripped).IsFalse();
    }

    [Test]
    public async Task Regression_TripsWhenComparisonExceedsThreshold()
    {
        var baseline = new BuildSnapshot { WallClockMs = 1000, TotalSelfTimeMs = 1000 };
        var current = new BuildSnapshot { WallClockMs = 1000, TotalSelfTimeMs = 1300 }; // +30% self
        var comparison = BuildComparison.Compare(baseline, current);

        await Assert.That(FailOnPolicy.Evaluate("regression:10", SampleReports.Minimal(), Analysis(), comparison).Tripped).IsTrue();
        await Assert.That(FailOnPolicy.Evaluate("regression:50", SampleReports.Minimal(), Analysis(), comparison).Tripped).IsFalse();
    }

    [Test]
    public async Task Regression_WithoutBaseline_TripsAsMisconfiguration()
    {
        var result = FailOnPolicy.Evaluate("regression:10", SampleReports.Minimal(), Analysis(), null);
        await Assert.That(result.Tripped).IsTrue();
    }

    [Test]
    public async Task UnknownRule_Trips()
    {
        var result = FailOnPolicy.Evaluate("bogus", SampleReports.Minimal(), Analysis(), null);
        await Assert.That(result.Tripped).IsTrue();
    }

    [Test]
    public async Task MultipleRules_AreCombined()
    {
        var report = SampleReports.Minimal(wallSeconds: 20);
        var result = FailOnPolicy.Evaluate("critical,wallclock:10", report, Analysis(FindingSeverity.Warning), null);
        // critical does not trip, but wallclock:10 does.
        await Assert.That(result.Tripped).IsTrue();
    }
}
