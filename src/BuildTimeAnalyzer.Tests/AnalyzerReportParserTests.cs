using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class AnalyzerReportParserTests
{
    private const string SampleReport = """
Total analyzer execution time: 12.345 seconds.
    Time (s)    %   Analyzer
      10.123   82   Microsoft.CodeAnalysis.NetAnalyzers, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
         5.001   40      Microsoft.CodeAnalysis.NetAnalyzers.SomeRule (CA1000)
       2.222   18   StyleCop.Analyzers, Version=1.2.0.0, Culture=neutral, PublicKeyToken=null

Total generator execution time: 3.200 seconds.
    Time (s)    %   Generator
       2.100   65   MyApp.SourceGen, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
       1.100   34   AnotherGen, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null
""";

    [Test]
    public async Task Parse_SingleMultiLineMessage_ExtractsAllEntries()
    {
        // Roslyn typically emits the entire ReportAnalyzer block as a single multi-line message.
        var messages = new[] { SampleReport };

        var report = AnalyzerReportParser.Parse("MyProject", TimeSpan.FromSeconds(15), messages);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.TotalAnalyzerTime).IsEqualTo(TimeSpan.FromSeconds(12.345));
        await Assert.That(report.TotalGeneratorTime).IsEqualTo(TimeSpan.FromSeconds(3.200));
        await Assert.That(report.Analyzers.Count).IsEqualTo(2);
        await Assert.That(report.Generators.Count).IsEqualTo(2);
        await Assert.That(report.Analyzers[0].AssemblyName).IsEqualTo("Microsoft.CodeAnalysis.NetAnalyzers");
        await Assert.That(report.Generators[0].AssemblyName).IsEqualTo("MyApp.SourceGen");
    }

    [Test]
    public async Task Parse_MessagesPerLine_ExtractsAllEntries()
    {
        var messages = SampleReport.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var report = AnalyzerReportParser.Parse("MyProject", TimeSpan.FromSeconds(15), messages);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Analyzers.Count).IsEqualTo(2);
        await Assert.That(report.Generators.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_NoReportAnalyzerOutput_ReturnsNull()
    {
        var messages = new[] { "Some unrelated message", "Another line" };

        var report = AnalyzerReportParser.Parse("MyProject", TimeSpan.FromSeconds(5), messages);

        await Assert.That(report).IsNull();
    }

    [Test]
    public async Task Parse_NarrowSpacingOnLargeTimes_StillExtractsEntry()
    {
        // When Time (s) is large (>=100s), columns can be separated by single spaces.
        var input = """
Total analyzer execution time: 150.000 seconds.
    Time (s)    %   Analyzer
     150.000 100 BigAnalyzer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
""";
        var report = AnalyzerReportParser.Parse("MyProject", TimeSpan.FromSeconds(200), new[] { input });

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Analyzers.Count).IsEqualTo(1);
        await Assert.That(report.Analyzers[0].AssemblyName).IsEqualTo("BigAnalyzer");
    }
}
