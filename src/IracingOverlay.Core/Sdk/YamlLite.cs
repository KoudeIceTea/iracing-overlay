using System.Globalization;

namespace IracingOverlay.Core.Sdk;

/// <summary>
/// A node in iRacing's session-info YAML: a map, a list, or a scalar.
/// Missing keys return <see cref="Empty"/> so lookups can be chained safely.
/// </summary>
public sealed class YamlNode
{
    public static readonly YamlNode Empty = new(null, null, null);

    private readonly Dictionary<string, YamlNode>? _map;
    private readonly List<YamlNode>? _list;

    private YamlNode(Dictionary<string, YamlNode>? map, List<YamlNode>? list, string? value)
    {
        _map = map;
        _list = list;
        Value = value;
    }

    internal static YamlNode Map(Dictionary<string, YamlNode> map) => new(map, null, null);
    internal static YamlNode List(List<YamlNode> list) => new(null, list, null);
    internal static YamlNode Scalar(string value) => new(null, null, value);

    public string? Value { get; }

    public YamlNode this[string key] =>
        _map != null && _map.TryGetValue(key, out var node) ? node : Empty;

    public IReadOnlyList<YamlNode> Items => _list ?? (IReadOnlyList<YamlNode>)Array.Empty<YamlNode>();

    /// <summary>Depth-first search for the first value stored under <paramref name="key"/> anywhere below this node.</summary>
    public YamlNode FindFirst(string key)
    {
        if (_map != null)
        {
            if (_map.TryGetValue(key, out var direct)) return direct;
            foreach (var child in _map.Values)
                if (child.FindFirst(key) is var found && found != Empty) return found;
        }
        if (_list != null)
            foreach (var child in _list)
                if (child.FindFirst(key) is var found && found != Empty) return found;
        return Empty;
    }

    public string Str(string fallback = "") => string.IsNullOrEmpty(Value) ? fallback : Value;

    /// <summary>Parses the leading number, so "138.12 sec" or "19.00 C" work.</summary>
    public float Float(float fallback = 0) =>
        float.TryParse(LeadingNumber(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public int Int(int fallback = 0) =>
        int.TryParse(LeadingNumber(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    /// <summary>Parses colors such as "0xffda59".</summary>
    public uint Hex(uint fallback = 0)
    {
        var v = Value?.Trim();
        if (string.IsNullOrEmpty(v)) return fallback;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        return uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) ? u : fallback;
    }

    private string LeadingNumber()
    {
        var v = Value?.Trim() ?? "";
        int space = v.IndexOf(' ');
        return space < 0 ? v : v[..space];
    }
}

/// <summary>
/// Indentation-based parser for the YAML subset iRacing emits. Deliberately lenient:
/// iRacing doesn't always quote values (driver/team names may contain ':' '#' etc.),
/// which trips up strict YAML libraries.
/// </summary>
public static class YamlLite
{
    private readonly record struct Line(int Indent, string Content);

    public static YamlNode Parse(string text)
    {
        var lines = new List<Line>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            var content = line[indent..];
            if (content is "---" or "...") continue;
            lines.Add(new Line(indent, content));
        }

        int i = 0;
        return lines.Count == 0 ? YamlNode.Empty : ParseMap(lines, ref i, lines[0].Indent);
    }

    private static bool IsListItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    private static YamlNode ParseBlock(List<Line> lines, ref int i)
    {
        var first = lines[i];
        return IsListItem(first.Content) ? ParseList(lines, ref i, first.Indent) : ParseMap(lines, ref i, first.Indent);
    }

    private static YamlNode ParseMap(List<Line> lines, ref int i, int indent)
    {
        var map = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Indent < indent || (line.Indent == indent && IsListItem(line.Content))) break;
            i++;
            if (line.Indent > indent) continue; // unexpected over-indentation; skip

            if (!TrySplitKey(line.Content, out var key, out var value)) continue;

            if (value.Length > 0)
            {
                map[key] = YamlNode.Scalar(Unquote(value));
            }
            else if (i < lines.Count &&
                     (lines[i].Indent > indent || (lines[i].Indent == indent && IsListItem(lines[i].Content))))
            {
                map[key] = ParseBlock(lines, ref i);
            }
            else
            {
                map[key] = YamlNode.Scalar("");
            }
        }
        return YamlNode.Map(map);
    }

    private static YamlNode ParseList(List<Line> lines, ref int i, int indent)
    {
        var list = new List<YamlNode>();
        while (i < lines.Count && lines[i].Indent == indent && IsListItem(lines[i].Content))
        {
            var content = lines[i].Content;
            var rest = content.Length > 1 ? content[1..].TrimStart() : "";

            if (rest.Length == 0)
            {
                i++;
                list.Add(i < lines.Count && lines[i].Indent > indent ? ParseBlock(lines, ref i) : YamlNode.Empty);
            }
            else if (TrySplitKey(rest, out _, out _))
            {
                // "- Key: value" starts a map whose remaining keys align with "Key".
                int itemIndent = indent + (content.Length - rest.Length);
                lines[i] = new Line(itemIndent, rest);
                list.Add(ParseMap(lines, ref i, itemIndent));
            }
            else
            {
                list.Add(YamlNode.Scalar(Unquote(rest)));
                i++;
            }
        }
        return YamlNode.List(list);
    }

    private static bool TrySplitKey(string content, out string key, out string value)
    {
        int idx = content.IndexOf(": ", StringComparison.Ordinal);
        if (idx > 0)
        {
            key = content[..idx];
            value = content[(idx + 2)..].Trim();
            return true;
        }
        if (content.EndsWith(':'))
        {
            key = content[..^1];
            value = "";
            return true;
        }
        key = value = "";
        return false;
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
}
