using System.Text;

namespace TaskbarPlayer;

/// <summary>Texts as music apps report them, made fit to show.</summary>
internal static class MediaText
{
    /// <summary>
    /// Every run of tabs, line breaks and other control characters, with the spaces around it, becomes one space:
    /// such characters draw as nothing and let words run together. Text without them is returned as it is.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        int first = 0;
        while (first < text.Length && !Breaks(text[first])) first++;
        if (first == text.Length) return text;

        var clean = new StringBuilder(text.Length);
        clean.Append(text, 0, first);
        bool gap = false;
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (Breaks(c))
            {
                // The spaces before the run go with it.
                while (clean.Length > 0 && clean[^1] == ' ') clean.Length--;
                gap = true;
            }
            else if (gap && c == ' ')
            {
                // And so do the spaces after it.
            }
            else
            {
                if (gap && clean.Length > 0) clean.Append(' ');
                gap = false;
                clean.Append(c);
            }
        }
        return clean.ToString();
    }

    // Control characters, and the two Unicode separators that also start a new line.
    private static bool Breaks(char c) => char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029;
}
