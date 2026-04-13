namespace BuildTimeAnalyzer.Rendering;

/// <summary>
/// Shared runtime toggle for whether live build output should stream to the console.
/// A background thread watches for Ctrl+E and flips <see cref="ShowOutput"/>; the build runner
/// and throbber observe <see cref="Toggled"/> to switch between live-streaming and capture modes.
/// </summary>
public sealed class BuildOutputController
{
    private volatile bool _showOutput;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private readonly object _eventLock = new();

    /// <summary>True while build output is being streamed live to the console.</summary>
    public bool ShowOutput => _showOutput;

    /// <summary>
    /// Raised whenever the user toggles the display state. The bool argument is the new state.
    /// Subscribers are invoked on the key-listener thread; keep handlers non-blocking.
    /// </summary>
    public event Action<bool>? Toggled;

    /// <summary>Start the background key listener. No-op when input is redirected (non-interactive).</summary>
    public void StartListening()
    {
        if (Console.IsInputRedirected) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _listenerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.E && (key.Modifiers & ConsoleModifiers.Control) != 0)
                        {
                            _showOutput = !_showOutput;
                            lock (_eventLock) Toggled?.Invoke(_showOutput);
                        }
                    }
                    await Task.Delay(60, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (InvalidOperationException) { break; }
            }
        }, ct);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_listenerTask is not null)
        {
            try { await _listenerTask; } catch { }
        }
        _cts.Dispose();
        _cts = null;
        _listenerTask = null;
    }
}
