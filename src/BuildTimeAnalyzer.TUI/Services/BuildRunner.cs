using System.Diagnostics;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

public sealed class BuildRunner
{
    // Cap the rolling tail so a runaway build can't eat memory.
    private const int CapturedTailLineLimit = 200;

    public async Task<BuildRunResult> RunAsync(
        string projectPath,
        string binLogPath,
        string configuration,
        bool incremental,
        BuildOutputController outputController,
        IEnumerable<string> extraArgs,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Build the argument vector element-by-element so paths and values containing spaces are
        // escaped correctly by the runtime. Passing a single joined string (Arguments) would split
        // "C:\My Projects\App.sln" into separate tokens and break the build.
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        psi.ArgumentList.Add($"-bl:{binLogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-p:ReportAnalyzer=true");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        // Default is --no-incremental so measurements are reproducible. Incremental builds skip most
        // targets when nothing changed, making the numbers depend on prior build state rather than
        // on the actual cost of your build.
        if (!incremental) psi.ArgumentList.Add("--no-incremental");
        foreach (var extra in extraArgs) psi.ArgumentList.Add(extra);

        // Force the compiler/CLI UI language to English. ReportAnalyzer output is parsed by
        // AnalyzerReportParser, which keys off English section labels and invariant-format numbers;
        // without this, a localized host emits translated labels / comma decimals and all
        // analyzer/generator data silently fails to parse.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

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

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            outputController.Toggled -= OnToggled;
            throw new InvalidOperationException(
                "Could not start 'dotnet'. Ensure the .NET SDK is installed and on your PATH " +
                $"(underlying error: {ex.Message}).", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // On Ctrl-C, kill the whole build process tree so no orphaned MSBuild nodes survive.
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            throw;
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
