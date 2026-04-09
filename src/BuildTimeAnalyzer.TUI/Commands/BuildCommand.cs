using System.ComponentModel;
using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildTimeAnalyzer.Commands;

public sealed class BuildCommandSettings : CommandSettings
{
    [CommandArgument(0, "[project]")]
    [Description("Path to the project or solution to build. Defaults to current directory.")]
    public string? ProjectPath { get; init; }

    [CommandOption("-c|--configuration")]
    [DefaultValue("Debug")]
    [Description("Build configuration (Debug/Release).")]
    public string Configuration { get; init; } = "Debug";

    [CommandOption("-n|--top")]
    [DefaultValue(20)]
    [Description("Number of top results to display.")]
    public int TopN { get; init; } = 20;

    [CommandOption("--keep-log")]
    [Description("Keep the .binlog file after analysis.")]
    public bool KeepLog { get; init; }

    [CommandOption("-o|--output")]
    [Description("Export report to file. Supported formats: .html, .json. Example: report.html")]
    public string? OutputPath { get; init; }

    [CommandOption("--args")]
    [Description("Additional arguments passed verbatim to dotnet build.")]
    public string? ExtraArgs { get; init; }
}

public sealed class BuildCommand : AsyncCommand<BuildCommandSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, BuildCommandSettings settings, CancellationToken cancellationToken)
    {
        var projectPath = settings.ProjectPath is { Length: > 0 }
            ? Path.GetFullPath(settings.ProjectPath)
            : Directory.GetCurrentDirectory();

        AnsiConsole.Write(new Rule("[bold blue]MSBuild Timing Analyzer[/]").RuleStyle("blue").LeftJustified());
        AnsiConsole.WriteLine();

        // ── 1. Run dotnet build ───────────────────────────────────────
        var runner = new BuildRunner();
        var extra = settings.ExtraArgs?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();

        var (exitCode, binLogPath) = await runner.RunAsync(projectPath, settings.Configuration, extra, cancellationToken);

        // ── 2. Analyze the binary log (streaming) ─────────────────────
        BuildReport? report = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue bold"))
            .StartAsync("[blue]Analyzing binary log…[/]", async ctx =>
            {
                var analyzer = new LogAnalyzer(settings.TopN);
                report = await analyzer.AnalyzeAsync(binLogPath, projectPath, cancellationToken);
            });

        if (report is not { } finalReport)
        {
            AnsiConsole.MarkupLine("[red]Failed to produce a report.[/]");
            return exitCode;
        }

        // ── 3. Render to console ──────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold blue]MSBuild Timing Analyzer[/]").RuleStyle("blue").LeftJustified());
        AnsiConsole.WriteLine();
        ConsoleReportRenderer.Render(finalReport, settings.TopN);

        // ── 3b. Automated analysis ─────────────────────────────────
        var analysis = BuildAnalyzer.Analyze(finalReport);
        ConsoleReportRenderer.RenderAnalysis(analysis);

        // ── 4. Optional export ────────────────────────────────────────
        if (settings.OutputPath is { Length: > 0 })
        {
            var ext = Path.GetExtension(settings.OutputPath).ToLowerInvariant();
            switch (ext)
            {
                case ".html":
                    HtmlReportExporter.Export(finalReport, settings.OutputPath, settings.TopN, analysis);
                    AnsiConsole.MarkupLine($"\n[green]✓[/] HTML report saved to [bold]{Markup.Escape(Path.GetFullPath(settings.OutputPath))}[/]");
                    break;

                case ".json":
                    JsonReportExporter.Export(finalReport, settings.OutputPath, analysis);
                    AnsiConsole.MarkupLine($"\n[green]✓[/] JSON report saved to [bold]{Markup.Escape(Path.GetFullPath(settings.OutputPath))}[/]");
                    break;

                default:
                    AnsiConsole.MarkupLine($"[yellow]⚠ Unknown export format '{Markup.Escape(ext)}'. Supported: .html, .json[/]");
                    break;
            }
        }

        // ── 5. Cleanup ────────────────────────────────────────────────
        if (!settings.KeepLog && File.Exists(binLogPath))
        {
            // BinLogReader may hold the file briefly after enumeration; retry a few times.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Delete(binLogPath);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(200, cancellationToken);
                }
            }
        }
        else if (settings.KeepLog)
        {
            AnsiConsole.MarkupLine($"\n[grey]Binary log kept at: {Markup.Escape(binLogPath)}[/]");
        }

        return exitCode;
    }
}
