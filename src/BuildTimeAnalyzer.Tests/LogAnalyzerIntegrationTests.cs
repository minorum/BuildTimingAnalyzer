using System.ComponentModel;
using System.Diagnostics;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

/// <summary>
/// End-to-end coverage for the core engine: build a trivial project to a real .binlog and parse it.
/// Skips (passes without asserting) when the .NET SDK is unavailable, so it never hard-fails an
/// environment that can't build — in CI (with the SDK installed) it exercises LogAnalyzer for real.
/// </summary>
public sealed class LogAnalyzerIntegrationTests
{
    [Test]
    public async Task Analyze_RealBinlog_ParsesTargetsAndSucceeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "btalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "Tiny.csproj");
        var binlog = Path.Combine(dir, "build.binlog");
        try
        {
            File.WriteAllText(csproj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
            File.WriteAllText(Path.Combine(dir, "Class1.cs"), "public class Class1 { public int N => 1; }");

            int exit;
            try
            {
                var psi = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in new[] { "build", csproj, $"-bl:{binlog}", "-nologo", "--no-incremental" })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                exit = p.ExitCode;
            }
            catch (Win32Exception)
            {
                return; // dotnet not on PATH — skip
            }

            if (exit != 0 || !File.Exists(binlog))
                return; // build failed for environmental reasons — skip rather than fail

            var report = await new LogAnalyzer().AnalyzeAsync(binlog, csproj);

            await Assert.That(report.Succeeded).IsTrue();
            await Assert.That(report.ExecutedTargetCount).IsGreaterThan(0);
            await Assert.That(report.Projects.Count).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
