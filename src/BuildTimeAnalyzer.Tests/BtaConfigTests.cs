using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class BtaConfigTests
{
    [Test]
    public async Task Parse_Empty_ReturnsDefaults()
    {
        var config = BtaConfig.Parse("{}");
        await Assert.That(config.HeavyPackages.Count).IsEqualTo(0);
        await Assert.That(config.Thresholds.LargestShareCriticalPercent).IsEqualTo(25.0);
        await Assert.That(config.Thresholds.WarningsOnCriticalPathPerProject).IsEqualTo(50);
    }

    [Test]
    public async Task Parse_HeavyPackages_AreCaptured()
    {
        var config = BtaConfig.Parse("""{ "heavyPackages": ["Foo.Bar", "Baz"] }""");
        await Assert.That(config.HeavyPackages.Count).IsEqualTo(2);
        await Assert.That(config.HeavyPackages.Contains("Foo.Bar")).IsTrue();
    }

    [Test]
    public async Task Parse_PartialThresholds_OverrideOnlySpecified()
    {
        var config = BtaConfig.Parse("""
            { "thresholds": { "largestShareCriticalPercent": 40, "costlyResolvePackageAssetsSeconds": 5 } }
            """);
        await Assert.That(config.Thresholds.LargestShareCriticalPercent).IsEqualTo(40.0);
        await Assert.That(config.Thresholds.CostlyResolvePackageAssetsSeconds).IsEqualTo(5.0);
        // Unspecified fields keep their defaults.
        await Assert.That(config.Thresholds.LargestShareWarningPercent).IsEqualTo(15.0);
    }

    [Test]
    public async Task Parse_NewThresholds_AreBound()
    {
        var config = BtaConfig.Parse("""
            {
              "thresholds": {
                "serializedBuildParallelismRatio": 0.7,
                "serializedBuildMinProjects": 8,
                "projectCountTaxMinProjects": 20,
                "projectCountTaxProjectSharePercent": 55
              }
            }
            """);
        await Assert.That(config.Thresholds.SerializedBuildParallelismRatio).IsEqualTo(0.7);
        await Assert.That(config.Thresholds.SerializedBuildMinProjects).IsEqualTo(8);
        await Assert.That(config.Thresholds.ProjectCountTaxMinProjects).IsEqualTo(20);
        await Assert.That(config.Thresholds.ProjectCountTaxProjectSharePercent).IsEqualTo(55.0);
    }

    [Test]
    public async Task Parse_CaseInsensitiveKeys()
    {
        var config = BtaConfig.Parse("""{ "HeavyPackages": ["X"] }""");
        await Assert.That(config.HeavyPackages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ConfigureHeavyPackages_ExtendsBuiltInSet()
    {
        try
        {
            ProjectPackageResolver.ConfigureHeavyPackages(["My.Custom.Package"]);
            await Assert.That(ProjectPackageResolver.IsHeavy("My.Custom.Package")).IsTrue();
            // Built-ins remain heavy.
            await Assert.That(ProjectPackageResolver.IsHeavy("Microsoft.EntityFrameworkCore")).IsTrue();
        }
        finally
        {
            ProjectPackageResolver.ResetHeavyPackages();
        }
    }

    [Test]
    public async Task Parse_UnknownKeys_ProduceWarnings()
    {
        var config = BtaConfig.Parse("""
            { "heavyPackages": [], "typo": 1, "thresholds": { "notAThreshold": 5 } }
            """);
        await Assert.That(config.Warnings.Count).IsEqualTo(2);
        await Assert.That(config.Warnings.Any(w => w.Contains("'typo'"))).IsTrue();
        await Assert.That(config.Warnings.Any(w => w.Contains("thresholds.notAThreshold"))).IsTrue();
    }

    [Test]
    public async Task Parse_KnownKeys_NoWarnings()
    {
        var config = BtaConfig.Parse("""{ "thresholds": { "largestShareWarningPercent": 12 } }""");
        await Assert.That(config.Warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultTemplate_RoundTripsWithDefaultsAndNoWarnings()
    {
        var config = BtaConfig.Parse(Commands.ConfigCommand.BuildDefaultConfigJson());
        await Assert.That(config.Warnings.Count).IsEqualTo(0);
        await Assert.That(config.Thresholds.LargestShareCriticalPercent).IsEqualTo(25.0);
        await Assert.That(config.Thresholds.SerializedBuildMinProjects).IsEqualTo(5);
        await Assert.That(config.Thresholds.ProjectCountTaxProjectSharePercent).IsEqualTo(40.0);
    }
}
