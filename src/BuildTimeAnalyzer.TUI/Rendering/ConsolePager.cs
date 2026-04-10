namespace BuildTimeAnalyzer.Rendering;

/// <summary>
/// Minimal terminal pager using ANSI escape codes. No third-party dependencies.
///
/// Features:
///   • Alternate screen buffer (\x1b[?1049h / \x1b[?1049l) — preserves scrollback on exit
///   • Cursor hide/show around the session
///   • Reverse-video status bar at the bottom
///   • Automatic fallback to direct output when stdout is redirected or the content fits
///   • Auto-detection of viewport resize between paints
///
/// Keys: Space / PgDn / f — forward page, b / PgUp — back page,
///       j / Down — line down, k / Up — line up,
///       g / Home — top, G / End — bottom, q / Esc — quit
/// </summary>
public static class ConsolePager
{
    public static void Display(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        // Non-interactive: write everything and return
        if (Console.IsOutputRedirected)
        {
            WriteAllPlain(lines);
            return;
        }

        int viewportHeight;
        try
        {
            viewportHeight = Math.Max(1, Console.WindowHeight - 1);
        }
        catch
        {
            // ReadKey won't work either — fall back to plain output
            WriteAllPlain(lines);
            return;
        }

        // If everything fits without scrolling, skip the pager ceremony
        if (lines.Count <= viewportHeight)
        {
            WriteAllPlain(lines);
            return;
        }

        try
        {
            RunPager(lines);
        }
        catch (InvalidOperationException)
        {
            // Console is not interactive (redirected / no tty). Fall back.
            WriteAllPlain(lines);
        }
        catch (PlatformNotSupportedException)
        {
            WriteAllPlain(lines);
        }
    }

    private static void WriteAllPlain(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
            Console.WriteLine(line);
    }

    private static void RunPager(IReadOnlyList<string> lines)
    {
        // Enter alternate screen buffer + hide cursor
        Console.Write("\x1b[?1049h");
        Console.Write("\x1b[?25l");

        try
        {
            int top = 0;
            int lastHeight = -1, lastWidth = -1, lastTop = -1;

            while (true)
            {
                int height = Math.Max(1, Console.WindowHeight - 1);
                int width = Math.Max(1, Console.WindowWidth);
                int maxTop = Math.Max(0, lines.Count - height);
                if (top > maxTop) top = maxTop;
                if (top < 0) top = 0;

                // Repaint only when something changed
                if (height != lastHeight || width != lastWidth || top != lastTop)
                {
                    Paint(lines, top, height, width);
                    lastHeight = height;
                    lastWidth = width;
                    lastTop = top;
                }

                var key = Console.ReadKey(intercept: true);
                var page = height; // one page = one full viewport

                switch (key.Key)
                {
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        return;

                    case ConsoleKey.Spacebar:
                    case ConsoleKey.PageDown:
                    case ConsoleKey.F:
                        top = Math.Min(top + page, maxTop);
                        break;

                    case ConsoleKey.B:
                    case ConsoleKey.PageUp:
                        top = Math.Max(0, top - page);
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                    case ConsoleKey.Enter:
                        top = Math.Min(top + 1, maxTop);
                        break;

                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        top = Math.Max(0, top - 1);
                        break;

                    case ConsoleKey.Home:
                        top = 0;
                        break;

                    case ConsoleKey.End:
                        top = maxTop;
                        break;

                    case ConsoleKey.G:
                        top = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? maxTop : 0;
                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            // Show cursor + leave alternate screen buffer
            Console.Write("\x1b[?25h");
            Console.Write("\x1b[?1049l");
        }
    }

    private static void Paint(IReadOnlyList<string> lines, int top, int height, int width)
    {
        // Cursor home + clear screen
        Console.Write("\x1b[H\x1b[2J");

        int end = Math.Min(top + height, lines.Count);
        for (int i = top; i < end; i++)
        {
            var line = lines[i];
            if (line.Length > width)
                line = line[..width];
            Console.WriteLine(line);
        }

        // Pad empty lines up to the status bar
        for (int i = end - top; i < height; i++)
            Console.WriteLine();

        // Status bar (reverse video)
        var pct = lines.Count > 0 ? (int)((double)end / lines.Count * 100) : 100;
        var status = $" {end}/{lines.Count} ({pct}%)   q=quit  space/pgdn=down  b/pgup=up  g=top  G=bottom ";
        if (status.Length > width) status = status[..width];
        else status = status.PadRight(width);

        Console.Write("\x1b[7m" + status + "\x1b[0m");
    }
}
