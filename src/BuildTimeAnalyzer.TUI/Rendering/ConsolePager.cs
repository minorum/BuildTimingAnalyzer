using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BuildTimeAnalyzer.Rendering;

/// <summary>
/// Pages output by spawning the system pager as a subprocess (like git, man, psql, kubectl).
/// The subprocess reads its content from our stdin pipe and handles keyboard input on its
/// own — we never touch keyboard input or write any terminal control sequences ourselves.
///
/// Pager selection (in priority order):
///   1. <c>BTANALYZER_PAGER</c> environment variable
///   2. <c>PAGER</c> environment variable
///   3. Platform default: <c>more</c> on Windows, <c>less -R -F</c> on Unix
///
/// This design deliberately avoids in-process pager implementations because doing so
/// would require linking against Win32 console input APIs that heuristic-based AV
/// scanners treat as suspicious for unsigned binaries.
/// </summary>
public static class ConsolePager
{
    public static void Display(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        // Non-interactive stdout: just write everything and return
        if (Console.IsOutputRedirected)
        {
            WriteAllPlain(lines);
            return;
        }

        var (fileName, arguments) = ResolvePagerCommand();
        if (string.IsNullOrEmpty(fileName))
        {
            WriteAllPlain(lines);
            return;
        }

        if (!TryRunPager(fileName, arguments, lines))
        {
            WriteAllPlain(lines);
        }
    }

    private static (string FileName, string Arguments) ResolvePagerCommand()
    {
        var userPager = Environment.GetEnvironmentVariable("BTANALYZER_PAGER");
        if (string.IsNullOrWhiteSpace(userPager))
            userPager = Environment.GetEnvironmentVariable("PAGER");

        if (!string.IsNullOrWhiteSpace(userPager))
            return SplitCommand(userPager);

        // Platform default
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("more", string.Empty);

        // -R: pass through colour codes (future-proofing)
        // -F: quit if the content fits in one screen (no prompt for short reports)
        return ("less", "-R -F");
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return (string.Empty, string.Empty);

        // Simple split on the first whitespace. For complex pager commands with quoted args,
        // the user can set PAGER to a shell wrapper (which is also the Unix convention).
        var firstSpace = command.IndexOf(' ');
        if (firstSpace < 0) return (command, string.Empty);
        return (command[..firstSpace], command[(firstSpace + 1)..]);
    }

    private static bool TryRunPager(string fileName, string arguments, IReadOnlyList<string> lines)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // Pager executable not found, not executable, or similar — fall back.
            return false;
        }

        if (process is null) return false;

        try
        {
            var writer = process.StandardInput;
            try
            {
                foreach (var line in lines)
                    writer.WriteLine(line);
            }
            catch (IOException)
            {
                // User pressed 'q' — the pager closed its stdin while we were still writing.
                // Normal termination; not an error.
            }
            finally
            {
                try { writer.Close(); } catch (IOException) { /* pipe may already be closed */ }
            }

            process.WaitForExit();
            return true;
        }
        catch
        {
            // If anything goes wrong while piping, surface the content via direct output instead.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
    }

    private static void WriteAllPlain(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
            Console.WriteLine(line);
    }
}
