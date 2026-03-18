using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Tests;

public sealed class FormatDurationTests
{
    [Test]
    public async Task SubSecond_ShowsMilliseconds()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(456));
        await Assert.That(result).IsEqualTo("456ms");
    }

    [Test]
    public async Task ExactlyOneMillisecond()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(1));
        await Assert.That(result).IsEqualTo("1ms");
    }

    [Test]
    public async Task SubMinute_ShowsSeconds()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromSeconds(12.345));
        await Assert.That(result).IsEqualTo("12.35s");
    }

    [Test]
    public async Task ExactlyOneSecond()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromSeconds(1));
        await Assert.That(result).IsEqualTo("1.00s");
    }

    [Test]
    public async Task MinutesAndSeconds()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromSeconds(125));
        await Assert.That(result).IsEqualTo("2m 05s");
    }

    [Test]
    public async Task Zero_ShowsZeroMs()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.Zero);
        await Assert.That(result).IsEqualTo("0ms");
    }

    [Test]
    public async Task LargeMinutes()
    {
        var result = ConsoleReportRenderer.FormatDuration(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(7));
        await Assert.That(result).IsEqualTo("10m 07s");
    }
}
