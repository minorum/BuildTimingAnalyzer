using System.Text;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Splits a raw <c>--args</c> string into individual tokens while respecting double quotes, so a
/// value containing spaces (e.g. <c>-p:DefineConstants="A B"</c>) survives as a single argument.
/// Each token is then handed to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
/// which re-escapes it for the OS.
/// </summary>
public static class CommandLineArgs
{
    public static IReadOnlyList<string> Split(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(input)) return result;

        var sb = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true; // a quote starts a token even if its content is empty ("")
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    hasToken = false;
                }
                continue;
            }
            sb.Append(c);
            hasToken = true;
        }

        if (hasToken) result.Add(sb.ToString());
        return result;
    }
}
