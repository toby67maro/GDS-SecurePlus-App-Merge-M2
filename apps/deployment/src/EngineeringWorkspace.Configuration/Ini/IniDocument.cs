using System.Text;

namespace EngineeringWorkspace.Configuration.Ini;

/// <summary>A problem found while reading an INI file, with its line number.</summary>
public sealed record IniParseError(int Line, string ErrorCode, string Message, string? Section, string? Key);

/// <summary>One key/value pair of an INI file.</summary>
public sealed record IniEntry(string Section, string Key, string Value, int Line);

/// <summary>
/// Line-preserving INI document. Reading reports every structural problem in one pass (malformed sections,
/// lines without '=', keys outside a section, duplicate keys, invalid encoding). Writing updates keys in place
/// and keeps comments, ordering and unrelated keys. Values follow Microsoft.Extensions.Configuration.Ini
/// semantics (surrounding double quotes are removed).
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(List<string> lines, IReadOnlyList<IniEntry> entries, IReadOnlyList<IniParseError> errors)
    {
        _lines = lines;
        Entries = entries;
        Errors = errors;
    }

    public IReadOnlyList<IniEntry> Entries { get; }
    public IReadOnlyList<IniParseError> Errors { get; }

    public static IniDocument Empty() => new([], [], []);

    /// <summary>Decodes bytes as UTF-8 (with or without BOM). Other encodings are reported as an error.</summary>
    public static IniDocument Load(byte[] content)
    {
        if (content.Length >= 2 && ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)))
            return new([], [], [new IniParseError(0, "UnsupportedEncoding", "The file is UTF-16 encoded. Save it as UTF-8.", null, null)]);
        var offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, offset, content.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return new([], [], [new IniParseError(0, "InvalidEncoding", "The file contains bytes that are not valid UTF-8. Save it as UTF-8.", null, null)]);
        }

        return Parse(text);
    }

    public static IniDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var entries = new List<IniEntry>();
        var errors = new List<IniParseError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '/') continue;
            if (line[0] == '[')
            {
                if (line[^1] != ']' || line.Length < 3)
                {
                    errors.Add(new(lineNumber, "MalformedSection", $"Line {lineNumber}: section header '{line}' must look like [SectionName].", section, null));
                    section = null;
                    continue;
                }

                section = line[1..^1].Trim();
                if (section.Length == 0)
                    errors.Add(new(lineNumber, "MalformedSection", $"Line {lineNumber}: the section name is empty.", null, null));
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                errors.Add(new(lineNumber, "MalformedLine", $"Line {lineNumber}: expected Key=Value.", section, null));
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (section is null)
            {
                errors.Add(new(lineNumber, "KeyOutsideSection", $"Line {lineNumber}: key '{key}' is not inside a [Section].", null, key));
                continue;
            }

            if (!seen.Add($"{section}:{key}"))
            {
                errors.Add(new(lineNumber, "DuplicateKey", $"Line {lineNumber}: key '{key}' appears more than once in [{section}].", section, key));
                continue;
            }

            entries.Add(new IniEntry(section, key, value, lineNumber));
        }

        return new IniDocument(lines, entries, errors);
    }

    /// <summary>Sets (or removes when <paramref name="value"/> is null) a key, keeping comments and layout.</summary>
    public void Set(string section, string key, string? value)
    {
        var sectionStart = -1;
        var sectionEnd = _lines.Count;
        for (var index = 0; index < _lines.Count; index++)
        {
            var trimmed = _lines[index].Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) continue;
            if (sectionStart >= 0)
            {
                sectionEnd = index;
                break;
            }

            if (string.Equals(trimmed[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase)) sectionStart = index;
        }

        if (sectionStart < 0)
        {
            if (value is null) return;
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            var trimmed = _lines[index].Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || trimmed[0] is ';' or '#') continue;
            if (!string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is null) _lines.RemoveAt(index);
            else _lines[index] = $"{key}={value}";
            return;
        }

        if (value is null) return;
        var insertAt = sectionEnd;
        while (insertAt > sectionStart + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
    }

    public string ToText() => string.Join("\r\n", _lines) + "\r\n";
}
