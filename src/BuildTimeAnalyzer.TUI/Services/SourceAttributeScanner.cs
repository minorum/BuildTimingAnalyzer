using System.Text.RegularExpressions;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Scans source files for attribute usage that confirms whether a source generator is doing
/// actual work. Used to answer the question "this generator cost 10 seconds — was it a no-op?".
/// </summary>
public static partial class SourceAttributeScanner
{
    private const int MaxBytesPerFile = 512 * 1024; // skip absurdly large generated files

    /// <summary>
    /// Matches <c>GeneratedComInterface</c> in attribute position, tolerating an optional namespace
    /// qualification, the optional <c>Attribute</c> suffix, and arguments — e.g.
    /// <c>[GeneratedComInterface]</c>, <c>[GeneratedComInterface(Options=…)]</c>,
    /// <c>[GeneratedComInterfaceAttribute]</c>,
    /// <c>[global::System.Runtime.InteropServices.Marshalling.GeneratedComInterface]</c>, and
    /// <c>[Guid("…"), GeneratedComInterface]</c>. The leading identifier boundary rejects
    /// <c>MyGeneratedComInterface</c>; the trailing <c>] ( ,</c> requires attribute position.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])GeneratedComInterface(Attribute)?\s*[\](,]", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedComInterfaceUsage();

    /// <summary>
    /// Returns project short names where at least one .cs file applies <c>[GeneratedComInterface]</c>.
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

            if (ContainsGeneratedComInterface(dir))
                result.Add(p.Name);
        }
        return result;
    }

    private static string? SafeGetDirectory(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private static bool ContainsGeneratedComInterface(string projectDir)
    {
        try
        {
            foreach (var file in EnumerateSourceFiles(projectDir))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxBytesPerFile) continue;
                    // Stream lines and early-exit on first match — avoids allocating the whole file
                    // as one string. Attribute text is ASCII so default UTF-8 decoding is fine.
                    foreach (var line in File.ReadLines(file))
                    {
                        if (GeneratedComInterfaceUsage().IsMatch(line))
                            return true;
                    }
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
