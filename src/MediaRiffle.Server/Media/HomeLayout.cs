namespace MediaRiffle.Server.Media;

/// <summary>
/// Which sections the home page shows and in what order, per user (Customize on the home page). Stored as the section
/// ids in order, a hidden one with a leading "-": "lists,categories,-libraries,continue,favorites".
/// </summary>
public static class HomeLayout
{
    /// <summary>The sections, in the order a user who never changed it sees them.</summary>
    public static readonly IReadOnlyList<string> Sections = ["lists", "favorites", "categories", "libraries", "continue"];

    /// <summary>
    /// A layout as sent by a client, made safe: unknown and repeated sections dropped, any missing ones added at the end
    /// (so a section added later still shows). Null when it is the default order with nothing hidden.
    /// </summary>
    /// <summary>The sections to send a client: the stored layout, or the default.</summary>
    public static string[] Of(string? stored) => stored is null ? [.. Sections] : stored.Split(',');

    public static string? Normalize(IEnumerable<string>? raw)
    {
        var seen = new List<string>();
        foreach (var item in raw ?? [])
        {
            var id = (item ?? "").Trim().ToLowerInvariant();
            var hidden = id.StartsWith('-');
            if (hidden) id = id[1..];
            if (!Sections.Contains(id) || seen.Any(s => s.TrimStart('-') == id)) continue;
            seen.Add(hidden ? "-" + id : id);
        }
        seen.AddRange(Sections.Where(s => !seen.Any(x => x.TrimStart('-') == s)));
        return seen.SequenceEqual(Sections) ? null : string.Join(',', seen);
    }
}
