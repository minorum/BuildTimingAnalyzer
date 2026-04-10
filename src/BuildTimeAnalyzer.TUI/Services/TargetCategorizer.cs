using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Deterministic pattern-matching from target name to <see cref="TargetCategory"/>.
/// This is a heuristic grouping hint, not an authoritative classification. The pattern
/// table is centralised here so it's easy to refine as new targets are encountered.
/// </summary>
public static class TargetCategorizer
{
    // Order matters: the first rule that matches wins.
    // Keep more specific rules above more general ones.
    private static readonly (string Pattern, MatchMode Mode, TargetCategory Category)[] Rules =
    [
        // ── Compilation ─────────────────────────────────────────────
        ("CoreCompile",                         MatchMode.Exact,     TargetCategory.Compile),
        ("Compile",                             MatchMode.Exact,     TargetCategory.Compile),
        ("Vbc",                                 MatchMode.Exact,     TargetCategory.Compile),
        ("Csc",                                 MatchMode.Exact,     TargetCategory.Compile),
        ("GenerateGlobalUsings",                MatchMode.Exact,     TargetCategory.Compile),
        ("CoreGenerateAssemblyInfo",            MatchMode.Exact,     TargetCategory.Compile),
        ("CreateGeneratedAssemblyInfoInputs",   MatchMode.StartsWith, TargetCategory.Compile),
        ("GenerateMSBuildEditorConfigFile",     MatchMode.StartsWith, TargetCategory.Compile),

        // ── Source generators / analyzers ───────────────────────────
        ("ResolveSourceGenerators",             MatchMode.Contains,  TargetCategory.SourceGen),
        ("ResolveOffByDefaultAnalyzers",        MatchMode.Exact,     TargetCategory.SourceGen),
        ("RunAnalyzers",                        MatchMode.Contains,  TargetCategory.SourceGen),

        // ── Static web assets (Blazor/Razor) ────────────────────────
        ("StaticWebAssets",                     MatchMode.Contains,  TargetCategory.StaticWebAssets),
        ("JSModule",                            MatchMode.Contains,  TargetCategory.StaticWebAssets),
        ("ResolveProjectReferencesDesign",      MatchMode.StartsWith, TargetCategory.StaticWebAssets),

        // ── Copy / output directory ─────────────────────────────────
        ("CopyFilesToOutputDirectory",          MatchMode.Exact,     TargetCategory.Copy),
        ("CopyFilesMarkedCopyLocal",            MatchMode.Contains,  TargetCategory.Copy),
        ("CopyFiles",                           MatchMode.StartsWith, TargetCategory.Copy),
        ("_CopyOut",                            MatchMode.StartsWith, TargetCategory.Copy),
        ("_CopyFiles",                          MatchMode.StartsWith, TargetCategory.Copy),
        ("CopyOutOfDateSourceItems",            MatchMode.Contains,  TargetCategory.Copy),
        ("IncrementalClean",                    MatchMode.Exact,     TargetCategory.Copy),

        // ── Restore / package resolution ────────────────────────────
        ("Restore",                             MatchMode.Exact,     TargetCategory.Restore),
        ("ResolvePackageAssets",                MatchMode.Exact,     TargetCategory.Restore),
        ("AddPrunePackageReferences",           MatchMode.Exact,     TargetCategory.Restore),
        ("GenerateBuildDependencyFile",         MatchMode.Exact,     TargetCategory.Restore),
        ("CheckForImplicitPackageReference",    MatchMode.StartsWith, TargetCategory.Restore),
        ("_GetRestoreSettings",                 MatchMode.StartsWith, TargetCategory.Restore),
        ("_GenerateProjectRestoreGraph",        MatchMode.StartsWith, TargetCategory.Restore),
        ("_FilterRestoreGraph",                 MatchMode.StartsWith, TargetCategory.Restore),
        ("_LoadRestoreGraph",                   MatchMode.StartsWith, TargetCategory.Restore),
        ("Restore",                             MatchMode.StartsWith, TargetCategory.Restore),

        // ── References / framework ──────────────────────────────────
        ("ResolveAssemblyReferences",           MatchMode.Exact,     TargetCategory.References),
        ("ResolveProjectReferences",            MatchMode.Exact,     TargetCategory.References),
        ("ResolveFrameworkReferences",          MatchMode.Exact,     TargetCategory.References),
        ("ResolveTargetingPackAssets",          MatchMode.Exact,     TargetCategory.References),
        ("FindReferenceAssemblies",             MatchMode.StartsWith, TargetCategory.References),
        ("ProcessFrameworkReferences",          MatchMode.Exact,     TargetCategory.References),
        ("_HandlePackageFileConflicts",         MatchMode.StartsWith, TargetCategory.References),
        ("_GetProjectReferenceTarget",          MatchMode.StartsWith, TargetCategory.References),

        // ── Internal SDK plumbing ───────────────────────────────────
        // These are well-known SDK targets that don't deserve to be surfaced as "potentially custom"
        ("PrepareForBuild",                     MatchMode.Exact,     TargetCategory.Other),
        ("InitializeSourceControlInformation",  MatchMode.StartsWith, TargetCategory.Other),
        ("TranslateBitbucketGitUrls",           MatchMode.StartsWith, TargetCategory.Other),
        ("TranslateGitHubUrls",                 MatchMode.StartsWith, TargetCategory.Other),
        ("TranslateGitLabUrls",                 MatchMode.StartsWith, TargetCategory.Other),
        ("TranslateAzureReposGitUrls",          MatchMode.StartsWith, TargetCategory.Other),
        ("ValidateSolutionConfiguration",       MatchMode.Exact,     TargetCategory.Other),
    ];

    public static TargetCategory Categorize(string targetName)
    {
        if (string.IsNullOrEmpty(targetName))
            return TargetCategory.Uncategorized;

        foreach (var (pattern, mode, category) in Rules)
        {
            var match = mode switch
            {
                MatchMode.Exact      => targetName.Equals(pattern, StringComparison.Ordinal),
                MatchMode.StartsWith => targetName.StartsWith(pattern, StringComparison.Ordinal),
                MatchMode.Contains   => targetName.Contains(pattern, StringComparison.Ordinal),
                _ => false,
            };
            if (match) return category;
        }

        // Internal MSBuild targets conventionally start with an underscore
        if (targetName.StartsWith('_'))
            return TargetCategory.Other;

        // Did not match any known pattern — honest naming
        return TargetCategory.Uncategorized;
    }

    private enum MatchMode { Exact, StartsWith, Contains }
}
