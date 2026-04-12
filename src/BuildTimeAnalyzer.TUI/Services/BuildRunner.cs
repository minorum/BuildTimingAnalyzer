using System.Diagnostics;

namespace BuildTimeAnalyzer.Services;

public sealed class BuildRunner
{
    public async Task<(int ExitCode, string BinLogPath)> RunAsync(
        string projectPath,
        string configuration,
        bool incremental,
        IEnumerable<string> extraArgs,
        CancellationToken ct = default)
    {
        var binLogPath = Path.Combine(Path.GetTempPath(), $"build-{Guid.NewGuid():N}.binlog");

        // Default is --no-incremental so measurements are reproducible.
        // Incremental builds skip most targets when nothing changed, which makes the numbers depend
        // on prior build state rather than on the actual cost of your build.
        var args = new List<string>
        {
            "build",
            projectPath,
            $"-c {configuration}",
            $"-bl:\"{binLogPath}\"",
            "-nologo",
            "-p:ReportAnalyzer=true",
            "-p:UseSharedCompilation=false",
        };
        if (!incremental) args.Add("--no-incremental");
        args.AddRange(extraArgs);

        var psi = new ProcessStartInfo("dotnet", string.Join(" ", args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Console.WriteLine($"Running: dotnet {string.Join(" ", args)}");
        Console.WriteLine();

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            PrintBuildLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Console.Error.WriteLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        Console.WriteLine();
        return (process.ExitCode, binLogPath);
    }

    private static void PrintBuildLine(string line)
    {
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored(line, ConsoleColor.Red);
        }
        else if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored(line, ConsoleColor.Yellow);
        }
        else if (line.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored(line, ConsoleColor.Green);
        }
        else if (line.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored(line, ConsoleColor.Red);
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
}
