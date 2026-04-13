using System.Diagnostics;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

public sealed class BuildRunner
{
    // Cap the rolling tail so a runaway build can't eat memory.
    private const int CapturedTailLineLimit = 200;

    public async Task<BuildRunResult> RunAsync(
        string projectPath,
        string configuration,
        bool incremental,
        BuildOutputController outputController,
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

        using var process = new Process { StartInfo = psi };

        // Rolling tail is always kept so we can (a) flush it on live-toggle and (b) dump it on failure.
        var capturedTail = new Queue<string>(CapturedTailLineLimit);
        var outputLock = new object();

        void FlushTail()
        {
            lock (outputLock)
            {
                foreach (var line in capturedTail) PrintBuildLine(line);
                capturedTail.Clear();
            }
        }

        // When the user toggles output on, flush whatever context we buffered so they see recent lines.
        void OnToggled(bool isOn)
        {
            if (isOn) FlushTail();
        }
        outputController.Toggled += OnToggled;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock)
            {
                if (outputController.ShowOutput)
                {
                    PrintBuildLine(e.Data);
                }
                // Always keep the tail up to date so failure dumps + toggle-on flushes have context.
                if (capturedTail.Count == CapturedTailLineLimit) capturedTail.Dequeue();
                capturedTail.Enqueue(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            // stderr always surfaces so real failures aren't silenced when stdout is hidden.
            if (e.Data is null) return;
            Console.Error.WriteLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            outputController.Toggled -= OnToggled;
        }

        IReadOnlyList<string> tail;
        lock (outputLock) tail = capturedTail.ToArray();

        return new BuildRunResult(process.ExitCode, binLogPath, tail);
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

/// <summary>
/// Result of a build invocation. <see cref="CapturedOutputTail"/> is the last N stdout lines
/// captured when build output was suppressed — empty otherwise. Useful for dumping context on failure.
/// </summary>
public sealed record BuildRunResult(int ExitCode, string BinLogPath, IReadOnlyList<string> CapturedOutputTail);
