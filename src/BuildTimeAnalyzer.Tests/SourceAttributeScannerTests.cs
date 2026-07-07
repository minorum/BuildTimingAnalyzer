using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class SourceAttributeScannerTests
{
    private static ProjectTiming ProjectAt(string csprojPath, string name) => new()
    {
        Name = name,
        FullPath = csprojPath,
        SelfTime = TimeSpan.FromSeconds(1),
        Succeeded = true,
        ErrorCount = 0,
        WarningCount = 0,
        StartOffset = TimeSpan.Zero,
        EndOffset = TimeSpan.FromSeconds(1),
    };

    [Test]
    public async Task FindsAttributeUsage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "btascan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Foo.cs"), """
                using System.Runtime.InteropServices.Marshalling;
                [GeneratedComInterface]
                internal partial interface IFoo { }
                """);
            var project = ProjectAt(Path.Combine(dir, "Proj.csproj"), "Proj");

            var result = SourceAttributeScanner.FindGeneratedComInterfaceUsages([project]);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0]).IsEqualTo("Proj");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task DoesNotMatchIdentifierPrefix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "btascan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Foo.cs"), "class MyGeneratedComInterfaceThing { }");
            var project = ProjectAt(Path.Combine(dir, "Proj.csproj"), "Proj");

            var result = SourceAttributeScanner.FindGeneratedComInterfaceUsages([project]);

            await Assert.That(result.Count).IsEqualTo(0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task SkipsObjAndBinDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), "btascan-" + Guid.NewGuid().ToString("N"));
        var objDir = Path.Combine(dir, "obj");
        Directory.CreateDirectory(objDir);
        try
        {
            // Only generated output under obj/ contains the attribute — must be ignored.
            File.WriteAllText(Path.Combine(objDir, "Generated.cs"), "[GeneratedComInterface] interface IX { }");
            var project = ProjectAt(Path.Combine(dir, "Proj.csproj"), "Proj");

            var result = SourceAttributeScanner.FindGeneratedComInterfaceUsages([project]);

            await Assert.That(result.Count).IsEqualTo(0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task MissingDirectory_ReturnsEmpty()
    {
        var project = ProjectAt(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"), "Proj.csproj"), "Proj");
        var result = SourceAttributeScanner.FindGeneratedComInterfaceUsages([project]);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
