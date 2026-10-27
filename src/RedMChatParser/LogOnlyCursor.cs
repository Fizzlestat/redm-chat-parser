using System.Globalization;
using System.Text;

namespace RedMChatParser;

internal static class LogLines
{
    private static readonly System.Text.RegularExpressions.Regex Timestamp = new(
        @"^\[(?<time>\d{1,2}:\d{2}:\d{2})\]\s+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool Same(string left, string right)
    {
        Split(left, out TimeSpan? leftTime, out string leftBody);
        Split(right, out TimeSpan? rightTime, out string rightBody);
        if (!string.Equals(Normalize(leftBody), Normalize(rightBody), StringComparison.Ordinal))
            return false;

        if (leftTime == null || rightTime == null)
            return true;

        return Math.Abs((leftTime.Value - rightTime.Value).TotalSeconds) <= 2;
    }

    public static string MatchKey(string line)
    {
        Split(line, out _, out string body);
        return Normalize(body);
    }

    public static void Split(string line, out TimeSpan? time, out string body)
    {
        System.Text.RegularExpressions.Match match = Timestamp.Match(line);
        if (!match.Success)
        {
            time = null;
            body = line;
            return;
        }

        body = line[match.Length..];
        time = TimeSpan.TryParseExact(
            match.Groups["time"].Value,
            ["h\\:mm\\:ss", "hh\\:mm\\:ss"],
            CultureInfo.InvariantCulture,
            out TimeSpan parsed)
            ? parsed
            : null;
    }

    public static bool TrySplitTranscript(string text, out string header, out List<string> lines)
    {
        header = string.Empty;
        lines = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int start = 0;
        if (parts.Length > 0 && ChatLog.TryParseHeader(parts[0].Trim(), out _))
        {
            header = parts[0].Trim();
            start = 1;
        }

        for (int i = start; i < parts.Length; i++)
        {
            string line = parts[i].Trim();
            if (line.Length == 0)
                continue;

            // A chat message can contain a newline (admin reports do). The chat box
            // shows that as one line. The session log stores the break as another
            // row with no timestamp, so join it back onto the line it belongs to.
            Split(line, out TimeSpan? time, out _);
            if (lines.Count > 0 && time == null)
            {
                lines[^1] = lines[^1] + " " + line;
                continue;
            }

            lines.Add(line);
        }

        return header.Length > 0;
    }

    private static string Normalize(string body)
    {
        StringBuilder builder = new StringBuilder(body.Length);
        bool pendingSpace = false;
        int index = 0;
        while (index < body.Length)
        {
            if (body[index] == '!' && index + 1 < body.Length && body[index + 1] == '{')
            {
                int end = body.IndexOf('}', index + 2);
                if (end > index)
                {
                    index = end + 1;
                    continue;
                }
            }

            if (body[index] == '~' && index + 2 < body.Length && body[index + 2] == '~' && char.IsLetter(body[index + 1]))
            {
                index += 3;
                continue;
            }

            if (body[index] == '^' && index + 1 < body.Length && char.IsDigit(body[index + 1]))
            {
                index += 2;
                continue;
            }

            if (body[index] == '^' && index + 7 < body.Length && body[index + 1] == '#' && IsHex(body, index + 2))
            {
                index += 8;
                continue;
            }

            if (body[index] == '/')
            {
                index++;
                continue;
            }

            if (char.IsWhiteSpace(body[index]))
            {
                pendingSpace = builder.Length > 0;
                index++;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(body[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool IsHex(string text, int start)
    {
        for (int i = 0; i < 6; i++)
        {
            char character = text[start + i];
            bool hex = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Appends session-log lines that never arrived through the chat box.
/// The first snapshot is a baseline so a log that already exists is not replayed.
/// </summary>
internal sealed class LogOnlyCursor
{
    private readonly List<string> _held = [];
    private readonly List<(string Line, long At)> _recent = [];
    private string? _header;
    private string? _anchor;
    private int _skip;
    private bool _ready;

    public void Reset()
    {
        _header = null;
        _anchor = null;
        _skip = 0;
        _ready = false;
        _held.Clear();
        _recent.Clear();
    }

    public void Remember(IReadOnlyList<string> lines)
    {
        long now = Environment.TickCount64;
        foreach (string line in lines)
            _recent.Add((line, now));
    }

    public bool ClaimDom(string plain)
    {
        long now = Environment.TickCount64;
        _recent.RemoveAll(item => now - item.At > 120_000);
        for (int i = 0; i < _recent.Count; i++)
        {
            if (!LogLines.Same(_recent[i].Line, plain))
                continue;

            _recent.RemoveAt(i);
            return true;
        }

        return false;
    }

    public List<string> Take(string? header, IReadOnlyList<string> logLines, IReadOnlyList<string> sessionLines)
    {
        header ??= string.Empty;
        if (!string.Equals(header, _header, StringComparison.Ordinal))
        {
            _header = header;
            _anchor = null;
            _skip = 0;
            _ready = false;
            _held.Clear();
            _recent.Clear();
        }

        if (!Resync(logLines))
            return [];

        if (!_ready)
        {
            _skip = logLines.Count;
            _anchor = _skip > 0 ? logLines[_skip - 1] : null;
            _ready = true;
            _held.Clear();
            return [];
        }

        bool[] matched = MatchSession(logLines, sessionLines, out int cursor);
        List<string> emit = new List<string>();
        List<string> tail = new List<string>();
        for (int i = _skip; i < logLines.Count; i++)
        {
            if (matched[i])
                continue;

            if (i < cursor)
                emit.Add(logLines[i]);
            else
                tail.Add(logLines[i]);
        }

        int matured = 0;
        while (matured < _held.Count && matured < tail.Count &&
               string.Equals(_held[matured], tail[matured], StringComparison.Ordinal))
        {
            matured++;
        }

        for (int i = 0; i < matured; i++)
            emit.Add(tail[i]);

        _held.Clear();
        for (int i = matured; i < tail.Count; i++)
            _held.Add(tail[i]);

        return emit;
    }

    private bool Resync(IReadOnlyList<string> logLines)
    {
        if (!_ready)
            return true;

        if (_skip <= logLines.Count &&
            (_skip == 0 || (_anchor != null && string.Equals(logLines[_skip - 1], _anchor, StringComparison.Ordinal))))
        {
            return true;
        }

        if (_anchor != null)
        {
            for (int i = logLines.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(logLines[i], _anchor, StringComparison.Ordinal))
                    continue;

                _skip = i + 1;
                _held.Clear();
                return true;
            }
        }

        _skip = logLines.Count;
        _anchor = _skip > 0 ? logLines[_skip - 1] : null;
        _held.Clear();
        return true;
    }

    private static bool[] MatchSession(IReadOnlyList<string> logLines, IReadOnlyList<string> sessionLines, out int cursor)
    {
        bool[] matched = new bool[logLines.Count];
        Dictionary<string, List<int>> positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < logLines.Count; i++)
        {
            string key = LogLines.MatchKey(logLines[i]);
            if (!positions.TryGetValue(key, out List<int>? list))
            {
                list = [];
                positions[key] = list;
            }

            list.Add(i);
        }

        Dictionary<string, int> next = new Dictionary<string, int>(StringComparer.Ordinal);
        cursor = 0;
        foreach (string sessionLine in sessionLines)
        {
            string key = LogLines.MatchKey(sessionLine);
            if (!positions.TryGetValue(key, out List<int>? list))
                continue;

            int start = next.GetValueOrDefault(key);
            int found = -1;
            int foundAt = start;
            for (int p = start; p < list.Count; p++)
            {
                if (matched[list[p]])
                    continue;

                if (!LogLines.Same(logLines[list[p]], sessionLine))
                    continue;

                found = list[p];
                foundAt = p + 1;
                break;
            }

            if (found < 0)
                continue;

            // The emote is appended at the end of the transcript, while the game log
            // keeps it between the chat lines. Matching has to find it there, or the
            // next poll treats it as new and appends it again.
            next[key] = foundAt;
            matched[found] = true;
            if (found + 1 > cursor)
                cursor = found + 1;
        }

        return matched;
    }
}
