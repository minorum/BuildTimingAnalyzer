using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class TargetCategorizerTests
{
    [Test]
    public async Task CoreCompile_IsCompile()
    {
        await Assert.That(TargetCategorizer.Categorize("CoreCompile")).IsEqualTo(TargetCategory.Compile);
    }

    [Test]
    public async Task CopyFilesToOutputDirectory_IsCopy()
    {
        await Assert.That(TargetCategorizer.Categorize("CopyFilesToOutputDirectory")).IsEqualTo(TargetCategory.Copy);
    }

    [Test]
    public async Task ResolvePackageAssets_IsRestore()
    {
        await Assert.That(TargetCategorizer.Categorize("ResolvePackageAssets")).IsEqualTo(TargetCategory.Restore);
    }

    [Test]
    public async Task ResolveAssemblyReferences_IsReferences()
    {
        await Assert.That(TargetCategorizer.Categorize("ResolveAssemblyReferences")).IsEqualTo(TargetCategory.References);
    }

    [Test]
    public async Task StaticWebAssetsTarget_IsStaticWebAssets()
    {
        await Assert.That(TargetCategorizer.Categorize("GenerateStaticWebAssetsManifest")).IsEqualTo(TargetCategory.StaticWebAssets);
    }

    [Test]
    public async Task InternalUnderscoreTarget_IsOther()
    {
        await Assert.That(TargetCategorizer.Categorize("_InternalBuildHelper")).IsEqualTo(TargetCategory.Other);
    }

    [Test]
    public async Task UnknownTargetWithoutUnderscore_IsUncategorized()
    {
        // Explicitly not Custom — honest naming
        await Assert.That(TargetCategorizer.Categorize("SomeUnknownUserTarget")).IsEqualTo(TargetCategory.Uncategorized);
    }

    [Test]
    public async Task AddReferenceToDashboardAndDCP_IsUncategorized()
    {
        // The punch list mentioned this as an example of a custom target that should be highlighted
        await Assert.That(TargetCategorizer.Categorize("AddReferenceToDashboardAndDCP")).IsEqualTo(TargetCategory.Uncategorized);
    }

    [Test]
    public async Task EmptyTargetName_IsUncategorized()
    {
        await Assert.That(TargetCategorizer.Categorize("")).IsEqualTo(TargetCategory.Uncategorized);
    }
}
