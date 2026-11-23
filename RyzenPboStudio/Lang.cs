using System.Globalization;
using System.Text;

namespace RyzenPboStudio;

/// <summary>
/// UI and log language. Text is written in English in the code as <c>L.T("…")</c>, or <c>L.F("… {0}", x)</c> with
/// placeholders; lang\&lt;code&gt;.ini maps that English text to a translation. The language is fixed at startup from
/// settings.json or, on "auto", the Windows display language; missing entries fall back to English.
/// </summary>
internal static class L
{
    public const string Auto = "auto", English = "en";

    private static Dictionary<string, string>? _table;

    public static string Code { get; private set; } = English;

    private static string LangDir => Path.Combine(AppContext.BaseDirectory, "lang");

    public static void Init(string setting)
    {
        Code = setting == Auto ? FromSystem() : setting;
        _table = Code == English ? null : Load(Path.Combine(LangDir, Code + ".ini"), out _);
        if (_table == null) Code = English;
    }

    /// <summary>English plus each lang\*.ini, as (code, name shown in its own language).</summary>
    public static List<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)> { (English, "English") };
        try
        {
            foreach (string file in Directory.GetFiles(LangDir, "*.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                if (code != English && Load(file, out string? name) != null) list.Add((code, name ?? code));
            }
        }
        catch { }
        return list;
    }

    /// <summary>The first lang file along the UI culture's parent chain, e.g. zh-CN → zh-Hans, zh-TW → zh-Hant.</summary>
    private static string FromSystem()
    {
        for (var c = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (File.Exists(Path.Combine(LangDir, c.Name + ".ini"))) return c.Name;
        return English;
    }

    public static string T(string en) => _table != null && _table.TryGetValue(en, out var s) ? s : en;

    /// <summary>Formats the translated template; a translation with broken placeholders falls back to English.</summary>
    public static string F(string en, params object?[] args)
    {
        try { return string.Format(T(en), args); }
        catch (FormatException) { return string.Format(en, args); }
    }

    /// <summary>
    /// Reads <c>English=translation</c> lines; [meta] holds <c>name=</c>. Lines starting with ; or # are comments,
    /// so English text that starts with them is written as \; or \#.
    /// \n, \t, \", \\ and \= are escapes; nothing is trimmed, so leading and trailing spaces are kept.
    /// </summary>
    private static Dictionary<string, string>? Load(string path, out string? name)
    {
        name = null;
        string[] lines;
        try { lines = File.ReadAllLines(path, Encoding.UTF8); }
        catch { return null; }

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        bool meta = false;
        foreach (string line in lines)
        {
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            int eq = SeparatorIndex(line);
            if (eq < 0)
            {
                if (line.StartsWith('[') && line.EndsWith(']')) meta = line.Equals("[meta]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            string key = Unescape(line[..eq]), value = Unescape(line[(eq + 1)..]);
            if (meta)
            {
                if (key == "name") name = value;
            }
            else if (key.Length > 0) table[key] = value;
        }
        return table;
    }

    private static int SeparatorIndex(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') i++;
            else if (line[i] == '=') return i;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
        }
        return sb.ToString();
    }
}
