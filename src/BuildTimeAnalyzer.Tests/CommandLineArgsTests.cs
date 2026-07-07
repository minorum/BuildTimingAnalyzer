using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class CommandLineArgsTests
{
    [Test]
    public async Task Split_NullOrEmpty_ReturnsEmpty()
    {
        await Assert.That(CommandLineArgs.Split(null).Count).IsEqualTo(0);
        await Assert.That(CommandLineArgs.Split("").Count).IsEqualTo(0);
        await Assert.That(CommandLineArgs.Split("   ").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Split_SimpleSpaceSeparated()
    {
        var result = CommandLineArgs.Split("--no-restore --verbosity quiet");
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0]).IsEqualTo("--no-restore");
        await Assert.That(result[2]).IsEqualTo("quiet");
    }

    [Test]
    public async Task Split_QuotedValueWithSpaces_StaysOneToken()
    {
        var result = CommandLineArgs.Split("-p:DefineConstants=\"A B\"");
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]).IsEqualTo("-p:DefineConstants=A B");
    }

    [Test]
    public async Task Split_MixedQuotedAndPlain()
    {
        var result = CommandLineArgs.Split("--no-restore -p:Foo=\"x y z\" --nologo");
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[1]).IsEqualTo("-p:Foo=x y z");
        await Assert.That(result[2]).IsEqualTo("--nologo");
    }

    [Test]
    public async Task Split_CollapsesRepeatedWhitespace()
    {
        var result = CommandLineArgs.Split("  a    b ");
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsEqualTo("a");
        await Assert.That(result[1]).IsEqualTo("b");
    }
}
