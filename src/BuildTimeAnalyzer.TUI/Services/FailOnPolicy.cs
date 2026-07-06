using System.Globalization;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Evaluates a <c>--fail-on</c> spec so btanalyzer can gate CI. The spec is a comma-separated list
/// of rules; if any rule trips, the process exits non-zero. Supported rules:
/// <list type="bullet">
///   <item><c>critical</c> — any Critical finding</item>
///   <item><c>warning</c> — any Warning or Critical finding</item>
///   <item><c>errors</c> — the build did not succeed cleanly</item>
///   <item><c>wallclock:&lt;seconds&gt;</c> — wall-clock exceeds N seconds</item>
///   <item><c>regression:&lt;percent&gt;</c> — worst of wall-clock/self-time regressed &gt; N% vs the --compare baseline (default 10)</item>
/// </list>
/// A misconfigured rule (unknown name, or <c>regression</c> without <c>--compare</c>) trips on
/// purpose — a silently-ignored CI gate is worse than a loud one.
/// </summary>
public static class FailOnPolicy
{
    public sealed record Result
    {
        public required bool Tripped { get; init; }
        public required IReadOnlyList<string> Reasons { get; init; }
    }

    public static Result Evaluate(
        string? spec,
        BuildReport report,
        BuildAnalysis analysis,
        BuildComparisonResult? comparison)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(spec))
            return new Result { Tripped = false, Reasons = reasons };

        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token == "critical")
            {
                var n = analysis.Findings.Count(f => f.Severity == FindingSeverity.Critical);
                if (n > 0) reasons.Add($"{n} critical finding(s)");
            }
            else if (token == "warning" || token == "warnings")
            {
                var n = analysis.Findings.Count(f => f.Severity is FindingSeverity.Warning or FindingSeverity.Critical);
                if (n > 0) reasons.Add($"{n} warning/critical finding(s)");
            }
            else if (token == "error" || token == "errors")
            {
                if (!report.Succeeded || report.ErrorCount > 0)
                    reasons.Add($"build not clean ({report.ErrorCount} error(s), succeeded={report.Succeeded})");
            }
            else if (token.StartsWith("wallclock", StringComparison.Ordinal))
            {
                var secs = ParseArg(token, defaultVal: 0);
                if (secs > 0 && report.TotalDuration.TotalSeconds > secs)
                    reasons.Add($"wall-clock {report.TotalDuration.TotalSeconds:F0}s exceeds {secs:F0}s");
            }
            else if (token.StartsWith("regression", StringComparison.Ordinal))
            {
                var pct = ParseArg(token, defaultVal: 10);
                if (comparison is null)
                    reasons.Add("regression rule set but no --compare baseline provided");
                else if (comparison.WorstRegressionPercent > pct)
                    reasons.Add(
                        $"regression {comparison.WorstRegressionPercent:F1}% exceeds {pct:F0}% " +
                        $"(wall-clock {comparison.WallClockDeltaPercent:F1}%, self-time {comparison.TotalSelfTimeDeltaPercent:F1}%)");
            }
            else
            {
                reasons.Add($"unknown --fail-on rule '{raw}'");
            }
        }

        return new Result { Tripped = reasons.Count > 0, Reasons = reasons };
    }

    // Parses the numeric argument from "name:VALUE"; returns defaultVal when absent/invalid.
    private static double ParseArg(string token, double defaultVal)
    {
        var idx = token.IndexOf(':');
        if (idx < 0 || idx + 1 >= token.Length) return defaultVal;
        return double.TryParse(token[(idx + 1)..], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultVal;
    }
}
