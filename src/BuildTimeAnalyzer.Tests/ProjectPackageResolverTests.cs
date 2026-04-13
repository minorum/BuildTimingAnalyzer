using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class ProjectPackageResolverTests
{
    [Test]
    public async Task Resolve_MissingFile_ReturnsNoCsprojQuality()
    {
        var result = ProjectPackageResolver.Resolve(@"C:\does\not\exist.csproj");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Quality).IsEqualTo(ProjectDataQuality.NoCsproj);
        await Assert.That(result.DirectPackages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_CsprojWithPackageReferences_ExtractsDirectPackages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "btatest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var csprojPath = Path.Combine(tempDir, "Sample.csproj");
        try
        {
            File.WriteAllText(csprojPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.0.0" />
    <ProjectReference Include="..\Other\Other.csproj" />
  </ItemGroup>
</Project>
""");

            var result = ProjectPackageResolver.Resolve(csprojPath);

            await Assert.That(result).IsNotNull();
            // No project.assets.json was created, so quality is CsprojOnly.
            await Assert.That(result!.Quality).IsEqualTo(ProjectDataQuality.CsprojOnly);
            await Assert.That(result.DirectPackages.Count).IsEqualTo(2);
            await Assert.That(result.ProjectReferences.Count).IsEqualTo(1);
            await Assert.That(result.ProjectReferences[0]).IsEqualTo("Other");

            var ef = result.DirectPackages.First(p => p.Id == "Microsoft.EntityFrameworkCore");
            await Assert.That(ef.IsKnownHeavy).IsTrue();
            await Assert.That(ef.Version).IsEqualTo("8.0.0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_MalformedCsproj_ReturnsNoCsprojQuality()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "btatest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var csprojPath = Path.Combine(tempDir, "Bad.csproj");
        try
        {
            File.WriteAllText(csprojPath, "<not valid xml");

            var result = ProjectPackageResolver.Resolve(csprojPath);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Quality).IsEqualTo(ProjectDataQuality.NoCsproj);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
