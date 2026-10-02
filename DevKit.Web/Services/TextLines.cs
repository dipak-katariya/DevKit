using System.Text;

namespace DevKit.Web.Services;

/// <summary>A file's non-blank lines as interned ids, with their original numbers and text for display.</summary>
public sealed class LineSequence
{
    public LineSequence(int[] ids, int[] numbers, string[] texts)
    {
        Ids = ids;
        Numbers = numbers;
        Texts = texts;
    }

    public int[] Ids { get; }

    /// <summary>1-based line numbers in the original file.</summary>
    public int[] Numbers { get; }

    public string[] Texts { get; }

    public int Count => Ids.Length;

    /// <summary>The line number to show for a position, including the position just past the end.</summary>
    public int NumberAt(int index)
    {
        if (Count == 0)
        {
            return 1;
        }
        return index < Count ? Numbers[index] : Numbers[^1] + 1;
    }
}

/// <summary>
/// Turns file text into comparable line ids. Comparison ignores whitespace and skips blank lines, so
/// re-indentation, a reformatted operator, CRLF against LF and an extra blank line — the usual
/// by-products of resolving a conflict by hand — never read as missing code.
/// </summary>
public sealed class LineInterner
{
    /// <summary>A line with fewer letters or digits than this (a brace, a semicolon) proves nothing on its own.</summary>
    private const int MinSignificantChars = 2;

    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<bool> _significant = new();
    private readonly StringBuilder _key = new();

    public LineSequence Map(string text)
    {
        var ids = new List<int>();
        var numbers = new List<int>();
        var texts = new List<string>();
        var number = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            number++;
            var key = Key(line);
            if (key.Length == 0)
            {
                continue;
            }
            ids.Add(Intern(key));
            numbers.Add(number);
            texts.Add(line.TrimEnd().ToString());
        }
        return new LineSequence(ids.ToArray(), numbers.ToArray(), texts.ToArray());
    }

    public bool IsSignificant(int id) => _significant[id];

    /// <summary>True when at least one line of the block is more than punctuation.</summary>
    public bool AnySignificant(ReadOnlySpan<int> block)
    {
        foreach (var id in block)
        {
            if (_significant[id])
            {
                return true;
            }
        }
        return false;
    }

    private string Key(ReadOnlySpan<char> line)
    {
        _key.Clear();
        foreach (var ch in line)
        {
            if (!char.IsWhiteSpace(ch))
            {
                _key.Append(ch);
            }
        }
        return _key.ToString();
    }

    private int Intern(string key)
    {
        if (_ids.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _ids.Count;
        _ids.Add(key, id);
        _significant.Add(key.Count(char.IsLetterOrDigit) >= MinSignificantChars);
        return id;
    }
}

/// <summary>Bytes of a file as text, or the verdict that it is binary.</summary>
public static class TextBlob
{
    /// <summary>How far into a file git looks for a NUL byte before calling it binary.</summary>
    private const int BinarySniffBytes = 8000;

    /// <summary>
    /// The file's text, or null for binary content. Invalid UTF-8 decodes to replacement characters
    /// rather than failing; the same bytes always decode the same way, so comparisons stay consistent.
    /// </summary>
    public static string? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinarySniffBytes)) >= 0)
        {
            return null;
        }

        var skip = StartsWith(bytes, 0xEF, 0xBB, 0xBF) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
