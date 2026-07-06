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
}
