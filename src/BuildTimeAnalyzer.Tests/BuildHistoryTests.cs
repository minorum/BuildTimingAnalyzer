using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class BuildHistoryTests
{
    private static BuildAnalysis EmptyAnalysis => new() { Findings = [], Recommendations = [] };
    private static readonly DateTime Ts = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Test]
    public async Task Append_ValidPath_WritesLineAndReturnsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ok = BuildHistory.Append(path, SampleReports.Minimal(), EmptyAnalysis, Ts);
            await Assert.That(ok).IsTrue();
            var lines = File.ReadAllLines(path);
            await Assert.That(lines.Length).IsEqualTo(1);
            await Assert.That(lines[0]).Contains("wallClockMs");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Append_UnwritablePath_ReturnsFalseWithoutThrowing()
    {
        // Directory does not exist — the write fails but must not throw.
        var path = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "hist.jsonl");
        var ok = BuildHistory.Append(path, SampleReports.Minimal(), EmptyAnalysis, Ts);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Append_AppendsRatherThanOverwrites()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}.jsonl");
        try
        {
            BuildHistory.Append(path, SampleReports.Minimal(), EmptyAnalysis, Ts);
            BuildHistory.Append(path, SampleReports.Minimal(), EmptyAnalysis, Ts);
            await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(2);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
