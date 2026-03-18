using System.Diagnostics;
using Spectre.Console;

namespace BuildTimeAnalyzer.Services;

public sealed class BuildRunner
{
    /// <summary>Runs dotnet build with binary log output. Returns exit code and path to .binlog file.</summary>
    public async Task<(int ExitCode, string BinLogPath)> RunAsync(
        string projectPath,
        string configuration,
        IEnumerable<string> extraArgs,
        CancellationToken ct = default)
    {
        var binLogPath = Path.Combine(Path.GetTempPath(), $"build-{Guid.NewGuid():N}.binlog");

        var args = new List<string>
        {
            "build",
            projectPath,
            $"-c {configuration}",
            $"-bl:\"{binLogPath}\"",
        };
        args.AddRange(extraArgs);

        var psi = new ProcessStartInfo("dotnet", string.Join(" ", args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        AnsiConsole.MarkupLine($"[grey]Running:[/] [bold]dotnet {string.Join(" ", args)}[/]");
        AnsiConsole.WriteLine();

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            PrintBuildLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            AnsiConsole.MarkupLineInterpolated($"[red]{Markup.Escape(e.Data)}[/]");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        AnsiConsole.WriteLine();
        return (process.ExitCode, binLogPath);
    }

    private static void PrintBuildLine(string line)
    {
        // Colour-code common MSBuild output patterns
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{Markup.Escape(line)}[/]");
        }
        else if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{Markup.Escape(line)}[/]");
        }
        else if (line.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLineInterpolated($"[bold green]{Markup.Escape(line)}[/]");
        }
        else if (line.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLineInterpolated($"[bold red]{Markup.Escape(line)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]{Markup.Escape(line)}[/]");
        }
    }
}
