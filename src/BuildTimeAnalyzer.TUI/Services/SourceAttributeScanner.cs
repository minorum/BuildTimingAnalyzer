using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Scans source files for attribute usage that confirms whether a source generator is doing
/// actual work. Used to answer the question "this generator cost 10 seconds — was it a no-op?".
/// </summary>
public static class SourceAttributeScanner
{
    private const int MaxBytesPerFile = 512 * 1024; // skip absurdly large generated files

    /// <summary>
    /// Returns project short names where at least one .cs file contains <c>[GeneratedComInterface]</c>.
    /// Silently skips projects whose directory is missing, unreadable, or contains no .cs files.
    /// </summary>
    public static IReadOnlyList<string> FindGeneratedComInterfaceUsages(IEnumerable<ProjectTiming> projects)
    {
        var result = new List<string>();
        foreach (var p in projects)
        {
            if (string.IsNullOrEmpty(p.FullPath)) continue;
            var dir = SafeGetDirectory(p.FullPath);
            if (dir is null || !Directory.Exists(dir)) continue;

            if (ContainsAttribute(dir, "[GeneratedComInterface]"))
                result.Add(p.Name);
        }
        return result;
    }

    private static string? SafeGetDirectory(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private static bool ContainsAttribute(string projectDir, string attribute)
    {
        try
        {
            foreach (var file in EnumerateSourceFiles(projectDir))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxBytesPerFile) continue;
                    // Read as UTF-8 (default). Attribute text is ASCII so encoding edge cases don't matter.
                    var text = File.ReadAllText(file);
                    if (text.Contains(attribute, StringComparison.Ordinal))
                        return true;
                }
                catch { /* per-file errors skipped silently */ }
            }
        }
        catch { /* directory enumeration errors skipped silently */ }
        return false;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDir)
    {
        // Enumerate lazily; skip obj/ and bin/ which contain generator output we'd double-count.
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.cs", opts))
        {
            var rel = f.AsSpan(projectDir.Length);
            if (ContainsSegment(rel, "obj") || ContainsSegment(rel, "bin"))
                continue;
            yield return f;
        }
    }

    private static bool ContainsSegment(ReadOnlySpan<char> path, string segment)
    {
        // Cross-platform path separator check without allocating.
        foreach (var sep in (ReadOnlySpan<char>)['\\', '/'])
        {
            var idx = 0;
            while (idx < path.Length)
            {
                var remaining = path[idx..];
                var sepIdx = remaining.IndexOf(sep);
                var part = sepIdx < 0 ? remaining : remaining[..sepIdx];
                if (part.Equals(segment, StringComparison.OrdinalIgnoreCase)) return true;
                if (sepIdx < 0) break;
                idx += sepIdx + 1;
            }
        }
        return false;
    }
}
