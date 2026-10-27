namespace RedMChatParser;

public sealed class ChatSession
{
    private ChatSession(DateTime startedAt)
    {
        StartedAt = startedAt;
    }

    public DateTime StartedAt { get; }

    public List<string> Lines { get; } = [];

    public List<string> DisplayLines { get; } = [];

    public bool IsDirty { get; private set; }

    public static ChatSession Start(string firstRawLine, DateTime capturedAt)
    {
        return new ChatSession(ChatLog.ResolveTimestamp(firstRawLine, capturedAt))
        {
            IsDirty = true
        };
    }

    public static ChatSession? TryLoad(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (!ChatLog.TryParseHeader(lines[0], out DateTime startedAt))
            return null;

        ChatSession session = new ChatSession(startedAt);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                session.Lines.Add(lines[i]);
                session.DisplayLines.Add(lines[i]);
            }
        }

        return session;
    }

    public void Add(IReadOnlyList<string> formattedLines, IReadOnlyList<string> coloredLines)
    {
        bool added = false;
        for (int i = 0; i < formattedLines.Count; i++)
        {
            string line = formattedLines[i];
            if (line.Length == 0)
                continue;

            string colored = i < coloredLines.Count ? coloredLines[i] : line;
            Lines.Add(line);
            DisplayLines.Add(colored.Length == 0 ? line : colored);
            added = true;
        }

        if (added)
            IsDirty = true;
    }

    public void PaintColors(IReadOnlyList<string> coloredLines)
    {
        for (int i = 0; i < Lines.Count && i < DisplayLines.Count; i++)
        {
            if (DisplayLines[i].Contains("^#", StringComparison.Ordinal) && IsOtherMessage(Lines[i], DisplayLines[i]))
                DisplayLines[i] = Lines[i];
        }

        int visible = 0;
        for (int i = 0; i < Lines.Count && visible < coloredLines.Count; i++)
        {
            if (!SameMessage(Lines[i], coloredLines[visible]))
                continue;

            string colored = coloredLines[visible++];
            if (i >= DisplayLines.Count || string.IsNullOrEmpty(colored) || !colored.Contains("^#", StringComparison.Ordinal))
                continue;

            DisplayLines[i] = colored;
        }
    }

    private static bool SameMessage(string plain, string colored)
    {
        return string.Equals(plain, ChatLog.StripColorCodes(colored).Trim(), StringComparison.Ordinal);
    }

    private static bool IsOtherMessage(string plain, string colored)
    {
        string stripped = ChatLog.StripColorCodes(colored).Trim();
        if (string.Equals(plain, stripped, StringComparison.Ordinal))
            return false;

        LogLines.Split(plain, out TimeSpan? plainTime, out _);
        LogLines.Split(stripped, out TimeSpan? coloredTime, out _);
        if (plainTime != null && coloredTime != null)
            return plainTime.Value != coloredTime.Value;

        return true;
    }

    public void PaintMatch(string plain, string colored)
    {
        if (string.IsNullOrEmpty(colored) || !colored.Contains("^#", StringComparison.Ordinal))
            return;

        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            if (!LogLines.Same(Lines[i], plain))
                continue;

            if (i < DisplayLines.Count)
                DisplayLines[i] = colored;
            return;
        }
    }

    public void MarkClean() => IsDirty = false;

    public string ToFileText() => ChatLog.Render(StartedAt, Lines);
}
