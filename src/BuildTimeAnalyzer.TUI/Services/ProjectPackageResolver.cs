using System.Text.Json;
using System.Xml.Linq;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Resolves direct and transitive package references for a single project by reading its
/// .csproj (direct packages + project references) and obj/project.assets.json (transitive
/// graph). Designed to silently degrade: missing files return the best-available
/// <see cref="ProjectPackages"/> with an honest <see cref="ProjectDataQuality"/> flag.
/// </summary>
public static class ProjectPackageResolver
{
    // Packages that are commonly responsible for large build cost (source generators, huge
    // transitive graphs, SDK framework references). Flagged in the UI when present.
    private static readonly HashSet<string> HeavyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.EntityFrameworkCore.Design",
        "Microsoft.EntityFrameworkCore.Tools",
        "Microsoft.AspNetCore.App",
        "Umbraco.Cms",
        "Umbraco.Cms.Core",
        "Umbraco.Cms.Web.UI",
        "Microsoft.Extensions.Telemetry",
        "OpenTelemetry",
        "Microsoft.Playwright",
    };

    public static ProjectPackages? Resolve(string csprojPath)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
        {
            return new ProjectPackages
            {
                Quality = ProjectDataQuality.NoCsproj,
                DirectPackages = [],
                TransitivePackages = [],
                ProjectReferences = [],
            };
        }

        var direct = new List<PackageRef>();
        var projectRefs = new List<string>();

        try
        {
            var doc = XDocument.Load(csprojPath);

            foreach (var e in doc.Descendants().Where(n => n.Name.LocalName == "PackageReference"))
            {
                var id = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                var version = e.Attribute("Version")?.Value
                              ?? e.Elements().FirstOrDefault(el => el.Name.LocalName == "Version")?.Value;
                direct.Add(new PackageRef
                {
                    Id = id,
                    Version = version,
                    Source = PackageReferenceSource.Direct,
                    IsKnownHeavy = HeavyPackages.Contains(id),
                });
            }

            foreach (var e in doc.Descendants().Where(n => n.Name.LocalName == "ProjectReference"))
            {
                var inc = e.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(inc)) continue;
                projectRefs.Add(Path.GetFileNameWithoutExtension(inc));
            }
        }
        catch
        {
            return new ProjectPackages
            {
                Quality = ProjectDataQuality.NoCsproj,
                DirectPackages = [],
                TransitivePackages = [],
                ProjectReferences = [],
            };
        }

        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        var transitive = ReadTransitiveFromAssets(assetsPath, direct);

        return new ProjectPackages
        {
            Quality = transitive is null ? ProjectDataQuality.CsprojOnly : ProjectDataQuality.Full,
            DirectPackages = direct,
            TransitivePackages = transitive ?? [],
            ProjectReferences = projectRefs,
        };
    }

    private static List<PackageRef>? ReadTransitiveFromAssets(string assetsPath, IReadOnlyList<PackageRef> directRefs)
    {
        if (!File.Exists(assetsPath)) return null;
        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
                return null;

            // Use the first TFM target. Most projects have just one; multi-targeting ones
            // pick the first deterministically rather than enumerating all (keeps the list tractable).
            var firstTarget = default(JsonElement);
            var hasFirst = false;
            foreach (var t in targets.EnumerateObject())
            {
                if (t.Value.ValueKind == JsonValueKind.Object) { firstTarget = t.Value; hasFirst = true; break; }
            }
            if (!hasFirst) return null;

            var directIds = new HashSet<string>(directRefs.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            // Build a parent lookup: package -> direct ancestor (one-level walk).
            var dependencyParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in firstTarget.EnumerateObject())
            {
                var slash = entry.Name.IndexOf('/');
                if (slash <= 0) continue;
                var id = entry.Name[..slash];
                if (!directIds.Contains(id)) continue;
                if (!entry.Value.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                foreach (var d in deps.EnumerateObject())
                    dependencyParents.TryAdd(d.Name, id);
            }

            var transitive = new List<PackageRef>();
            foreach (var entry in firstTarget.EnumerateObject())
            {
                var slash = entry.Name.IndexOf('/');
                if (slash <= 0) continue;
                var id = entry.Name[..slash];
                var version = entry.Name[(slash + 1)..];
                if (directIds.Contains(id)) continue;
                // Skip project-type entries (e.g. "type":"project") — those are ProjectReferences.
                if (entry.Value.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    string.Equals(typeEl.GetString(), "project", StringComparison.Ordinal))
                    continue;

                transitive.Add(new PackageRef
                {
                    Id = id,
                    Version = version,
                    Source = PackageReferenceSource.Transitive,
                    ParentPackage = dependencyParents.GetValueOrDefault(id),
                    IsKnownHeavy = HeavyPackages.Contains(id),
                });
            }
            return transitive;
        }
        catch
        {
            return null;
        }
    }
}
