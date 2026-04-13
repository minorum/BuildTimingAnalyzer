using System.Diagnostics;

namespace BuildTimeAnalyzer.Rendering;

/// <summary>
/// Single-line spinner for long-running steps. On a TTY, animates in place and
/// replaces the line with a check mark + elapsed time when stopped. On a
/// redirected/CI output, degrades to a single start line + single done line so
/// logs stay readable.
/// </summary>
public sealed class Throbber : IAsyncDisposable
{
    // Braille spinner — renders cleanly on modern Windows Terminal, iTerm2, gnome-terminal.
    private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
    private const int FrameDelayMs = 90;

    private readonly string _message;
    private readonly bool _animate;
    private readonly Stopwatch _stopwatch;
    private CancellationTokenSource _cts = new();
    private Task _loop;
    private bool _stopped;
    private volatile bool _paused;

    public Throbber(string message)
    {
        _message = message;
        _animate = !Console.IsOutputRedirected;
        _stopwatch = Stopwatch.StartNew();

        if (!_animate)
        {
            Console.WriteLine($"==> {message}");
            _loop = Task.CompletedTask;
            return;
        }

        _loop = Task.Run(AnimateAsync);
    }

    /// <summary>Suspend animation and clear the spinner line. Call <see cref="Resume"/> to restart.</summary>
    public void Pause()
    {
        if (!_animate || _stopped || _paused) return;
        _paused = true;
        _cts.Cancel();
        try { _loop.Wait(200); } catch { }
        ClearLine();
    }

    /// <summary>Restart animation after a <see cref="Pause"/>. Elapsed time continues accumulating.</summary>
    public void Resume()
    {
        if (!_animate || _stopped || !_paused) return;
        _paused = false;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(AnimateAsync);
    }

    private async Task AnimateAsync()
    {
        int i = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = Frames[i++ % Frames.Length];
                Console.Write($"\r{frame} {_message}");
                await Task.Delay(FrameDelayMs, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Stop the spinner and print a done marker with elapsed time.</summary>
    public async Task StopAsync(string? doneSuffix = null)
    {
        if (_stopped) return;
        _stopped = true;
        _stopwatch.Stop();
        _cts.Cancel();
        try { await _loop; } catch { }

        var elapsed = FormatElapsed(_stopwatch.Elapsed);
        var suffix = doneSuffix is { Length: > 0 } ? $" — {doneSuffix}" : "";

        if (_animate)
        {
            ClearLine();
            Console.WriteLine($"✓ {_message}{suffix} ({elapsed})");
        }
        else
        {
            Console.WriteLine($"    done{suffix} ({elapsed})");
        }
    }

    private void ClearLine()
    {
        // Write enough spaces to wipe the spinner line without assuming terminal width.
        var blank = new string(' ', Math.Min(120, Math.Max(20, _message.Length + 8)));
        Console.Write($"\r{blank}\r");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static string FormatElapsed(TimeSpan ts) =>
        ts.TotalSeconds < 1
            ? $"{ts.TotalMilliseconds:F0}ms"
            : ts.TotalSeconds < 60
                ? $"{ts.TotalSeconds:F1}s"
                : $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
}
