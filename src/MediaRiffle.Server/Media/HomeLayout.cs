namespace MediaRiffle.Server.Media;

/// <summary>
/// Which sections each top tab (Videos, Music) shows and in what order, per user (Customize on the tab). Stored as each
/// changed tab's section ids in order, a hidden one with a leading "-": "videos:continue,-libraries,lists;music:…".
/// Before the tabs there was one home page, stored as "lists,categories,…" alone: that is read as every tab's.
/// </summary>
public static class HomeLayout
{
    /// <summary>Each tab's sections, in the order a user who never changed it sees them.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Tabs = new Dictionary<string, IReadOnlyList<string>>
    {
        ["videos"] = ["lists", "favorites", "categories", "libraries", "continue"],
        ["music"] = ["lists", "favorites", "categories", "libraries"],
    };

    /// <summary>Every tab's sections to send a client: the stored layout where there is one, else the default.</summary>
    public static Dictionary<string, string[]> Of(string? stored)
    {
        var parsed = Parse(stored);
        return Tabs.ToDictionary(t => t.Key, t => Normalize(t.Key, parsed.GetValueOrDefault(t.Key)));
    }

    /// <summary>
    /// The stored value with one tab's layout changed to what a client sent (made safe by <see cref="Normalize"/>). Null
    /// when every tab is back to its default.
    /// </summary>
    public static string? With(string? stored, string tab, IEnumerable<string>? raw)
    {
        var layouts = Of(stored);
        layouts[tab] = Normalize(tab, raw);
        var changed = layouts.Where(l => !l.Value.SequenceEqual(Tabs[l.Key])).ToList();
        return changed.Count == 0 ? null : string.Join(';', changed.Select(l => $"{l.Key}:{string.Join(',', l.Value)}"));
    }

    /// <summary>
    /// A tab's layout made safe: unknown and repeated sections dropped, any missing ones added at the end (so a section
    /// added later still shows).
    /// </summary>
    public static string[] Normalize(string tab, IEnumerable<string>? raw)
    {
        var sections = Tabs[tab];
        var seen = new List<string>();
        foreach (var item in raw ?? [])
        {
            var id = (item ?? "").Trim().ToLowerInvariant();
            var hidden = id.StartsWith('-');
            if (hidden) id = id[1..];
            if (!sections.Contains(id) || seen.Any(s => s.TrimStart('-') == id)) continue;
            seen.Add(hidden ? "-" + id : id);
        }
        seen.AddRange(sections.Where(s => !seen.Any(x => x.TrimStart('-') == s)));
        return [.. seen];
    }

    private static Dictionary<string, string[]> Parse(string? stored)
    {
        var result = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(stored)) return result;
        // The home page's layout from before the tabs: each tab keeps the part of it that it has
        if (!stored.Contains(':')) return Tabs.Keys.ToDictionary(t => t, _ => stored.Split(','));
        foreach (var part in stored.Split(';'))
        {
            var at = part.IndexOf(':');
            if (at > 0 && Tabs.ContainsKey(part[..at])) result[part[..at]] = part[(at + 1)..].Split(',');
        }
        return result;
    }
}
