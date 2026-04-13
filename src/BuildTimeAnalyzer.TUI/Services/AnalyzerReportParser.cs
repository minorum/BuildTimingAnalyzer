using System.Globalization;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Parses the ReportAnalyzer text output emitted by Csc/Vbc into the binlog when
/// <c>-p:ReportAnalyzer=true</c> is set. We always set this flag, so this data
/// is always present in our binlogs.
///
/// Text format (from Roslyn's ReportAnalyzerUtil.cs):
///   Total analyzer execution time: 12.345 seconds.
///       Time (s)    %   Analyzer
///         10.123   82   &lt;Assembly FullName&gt;
///            5.001   40      &lt;AnalyzerType.FullName&gt; (DIAG1, DIAG2)
///
///   Total generator execution time: 3.200 seconds.
///       Time (s)    %   Generator
///          2.100   65   &lt;Assembly FullName&gt;
/// </summary>
public static class AnalyzerReportParser
{
    public static AnalyzerReport? Parse(string projectName, TimeSpan cscWallTime, IReadOnlyList<string> messages)
    {
        var analyzers = new List<AnalyzerEntry>();
        var generators = new List<AnalyzerEntry>();
        TimeSpan totalAnalyzerTime = TimeSpan.Zero;
        TimeSpan totalGeneratorTime = TimeSpan.Zero;

        bool foundAny = false;

        for (int i = 0; i < messages.Count; i++)
        {
            var line = messages[i];
            if (line.Contains("Total analyzer execution time:", StringComparison.OrdinalIgnoreCase))
            {
                totalAnalyzerTime = ParseTotalLine(line);
                foundAny = true;
                ParseEntries(messages, i + 1, analyzers);
            }
            else if (line.Contains("Total generator execution time:", StringComparison.OrdinalIgnoreCase))
            {
                totalGeneratorTime = ParseTotalLine(line);
                foundAny = true;
                ParseEntries(messages, i + 1, generators);
            }
        }

        if (!foundAny) return null;

        return new AnalyzerReport
        {
            ProjectName = projectName,
            TotalAnalyzerTime = totalAnalyzerTime,
            TotalGeneratorTime = totalGeneratorTime,
            CscWallTime = cscWallTime,
            Analyzers = analyzers,
            Generators = generators,
        };
    }

    private static TimeSpan ParseTotalLine(string line)
    {
        // "Total analyzer execution time: 12.345 seconds."
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return TimeSpan.Zero;
        var afterColon = line[(colonIdx + 1)..].Trim();
        var spaceIdx = afterColon.IndexOf(' ');
        if (spaceIdx < 0) return TimeSpan.Zero;
        var numStr = afterColon[..spaceIdx];
        if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.Zero;
    }

    private static void ParseEntries(IReadOnlyList<string> messages, int startIndex, List<AnalyzerEntry> entries)
    {
        // Skip the header line "    Time (s)    %   Analyzer" if present
        for (int i = startIndex; i < messages.Count; i++)
        {
            var line = messages[i].Trim();
            if (line.Length == 0) break;
            if (line.StartsWith("Time (s)", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("Total analyzer execution time:", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Contains("Total generator execution time:", StringComparison.OrdinalIgnoreCase)) break;

            // Only assembly-level rows (contain ", Version=")
            if (!line.Contains(", Version=", StringComparison.Ordinal)) continue;

            var entry = ParseEntryLine(line);
            if (entry is not null) entries.Add(entry);
        }
    }

    private static AnalyzerEntry? ParseEntryLine(string line)
    {
        // "      10.123   82   Some.Assembly, Version=1.0.0.0, ..."
        // Split by whitespace, expect at least 3 parts (time, percent, assembly name)
        var trimmed = line.Trim();
        var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            // "<1" case
            if (parts[1].Trim().StartsWith("<"))
                percent = 0.5;
            else
                return null;
        }

        var assemblyFull = parts[2].Trim();
        // Shorten to just the assembly short name
        var commaIdx = assemblyFull.IndexOf(',');
        var shortName = commaIdx > 0 ? assemblyFull[..commaIdx].Trim() : assemblyFull;

        return new AnalyzerEntry
        {
            AssemblyName = shortName,
            Time = TimeSpan.FromSeconds(seconds),
            Percent = percent,
        };
    }
}
