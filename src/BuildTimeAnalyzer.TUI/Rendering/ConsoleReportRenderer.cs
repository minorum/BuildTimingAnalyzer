using BuildTimeAnalyzer.Models;
using Spectre.Console;

namespace BuildTimeAnalyzer.Rendering;

public static class ConsoleReportRenderer
{
    public static void Render(BuildReport report, int topN)
    {
        RenderSummaryPanel(report);
        AnsiConsole.WriteLine();
        RenderProjectsTable(report, topN);
        AnsiConsole.WriteLine();
        RenderTargetsTable(report);
    }

    // ──────────────────────────── Summary ────────────────────────────

    private static void RenderSummaryPanel(BuildReport report)
    {
        var statusMarkup = report.Succeeded
            ? "[bold green]✓ Build Succeeded[/]"
            : "[bold red]✗ Build Failed[/]";

        var grid = new Grid().AddColumns(2);

        void Row(string label, Markup value)
            => grid.AddRow(new Markup($"[grey]{label}[/]"), value);

        Row("Status", new Markup(statusMarkup));
        Row("Project", new Markup(Markup.Escape(Path.GetFileName(report.ProjectOrSolutionPath))));
        Row("Started", new Markup(report.StartTime.ToString("HH:mm:ss")));
        Row("Duration", new Markup($"[bold]{FormatDuration(report.TotalDuration)}[/]"));
        Row("Errors", new Markup(report.ErrorCount > 0 ? $"[red]{report.ErrorCount}[/]" : "0"));
        Row("Warnings", new Markup(report.WarningCount > 0 ? $"[yellow]{report.WarningCount}[/]" : "0"));
        Row("Projects", new Markup(report.Projects.Count.ToString()));

        var borderColor = report.Succeeded ? Color.Green : Color.Red;
        var panel = new Panel(grid)
        {
            Header = new PanelHeader(" [bold]Build Report[/] "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Padding = new Padding(1, 0),
        };

        AnsiConsole.Write(panel);
    }

    // ──────────────────────────── Projects ───────────────────────────

    private static void RenderProjectsTable(BuildReport report, int topN)
    {
        var projects = report.Projects.Take(topN).ToList();
        if (projects.Count == 0) return;

        var table = new Table()
            .Title("[bold blue]⏱ Top Projects by Duration[/]")
            .BorderColor(Color.Grey)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Project[/]"))
            .AddColumn(new TableColumn("[bold]Duration[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]% of Build[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Share[/]"))
            .AddColumn(new TableColumn("[bold]Errors[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Warnings[/]").RightAligned());

        int rank = 1;
        foreach (var p in projects)
        {
            var barWidth = 20;
            var filled = (int)Math.Round(p.Percentage / 100.0 * barWidth);
            var bar = new string('█', filled) + new string('░', barWidth - filled);
            var barColor = p.Percentage > 50 ? "red" : p.Percentage > 20 ? "yellow" : "green";

            var statusIcon = p.Succeeded ? "" : "[red]✗[/] ";
            var errCol = p.ErrorCount > 0 ? $"[red]{p.ErrorCount}[/]" : "[grey]0[/]";
            var warnCol = p.WarningCount > 0 ? $"[yellow]{p.WarningCount}[/]" : "[grey]0[/]";

            table.AddRow(
                new Markup($"[grey]{rank++}[/]"),
                new Markup($"{statusIcon}[white]{Markup.Escape(p.Name)}[/]"),
                new Markup($"[bold]{FormatDuration(p.Duration)}[/]"),
                new Markup($"{p.Percentage:F1}%"),
                new Markup($"[{barColor}]{bar}[/]"),
                new Markup(errCol),
                new Markup(warnCol)
            );
        }

        AnsiConsole.Write(table);
    }

    // ──────────────────────────── Targets ────────────────────────────

    private static void RenderTargetsTable(BuildReport report)
    {
        if (report.TopTargets.Count == 0) return;

        var table = new Table()
            .Title("[bold blue]🎯 Slowest Targets[/]")
            .BorderColor(Color.Grey)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Target[/]"))
            .AddColumn(new TableColumn("[bold]Project[/]"))
            .AddColumn(new TableColumn("[bold]Duration[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]% of Build[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Share[/]"));

        int rank = 1;
        foreach (var t in report.TopTargets)
        {
            var barWidth = 20;
            var filled = (int)Math.Round(t.Percentage / 100.0 * barWidth);
            var bar = new string('█', filled) + new string('░', barWidth - filled);
            var barColor = t.Percentage > 30 ? "red" : t.Percentage > 10 ? "yellow" : "green";

            table.AddRow(
                new Markup($"[grey]{rank++}[/]"),
                new Markup($"[cyan]{Markup.Escape(t.Name)}[/]"),
                new Markup($"[grey]{Markup.Escape(t.ProjectName)}[/]"),
                new Markup($"[bold]{FormatDuration(t.Duration)}[/]"),
                new Markup($"{t.Percentage:F1}%"),
                new Markup($"[{barColor}]{bar}[/]")
            );
        }

        AnsiConsole.Write(table);
    }

    // ──────────────────────────── Analysis ───────────────────────────

    public static void RenderAnalysis(BuildAnalysis analysis)
    {
        if (analysis.Findings.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold blue]Analysis[/]").RuleStyle("blue").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Key Findings[/]");
        AnsiConsole.WriteLine();

        foreach (var f in analysis.Findings)
        {
            var color = f.Severity switch
            {
                FindingSeverity.Critical => "red",
                FindingSeverity.Warning => "yellow",
                _ => "blue",
            };
            AnsiConsole.MarkupLine($"  [{color}]{f.Number}.[/] [bold]{Markup.Escape(f.Title)}[/]");
            AnsiConsole.MarkupLine($"     [grey]{Markup.Escape(f.Detail)}[/]");
            AnsiConsole.WriteLine();
        }

        if (analysis.Recommendations.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Recommendations[/]");
            AnsiConsole.WriteLine();
            foreach (var r in analysis.Recommendations)
            {
                AnsiConsole.MarkupLine($"  [green]{r.Number}.[/] {Markup.Escape(r.Text)}");
            }
            AnsiConsole.WriteLine();
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return FormattableString.Invariant($"{ts.TotalMilliseconds:F0}ms");
        if (ts.TotalMinutes < 1) return FormattableString.Invariant($"{ts.TotalSeconds:F2}s");
        return FormattableString.Invariant($"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s");
    }
}
