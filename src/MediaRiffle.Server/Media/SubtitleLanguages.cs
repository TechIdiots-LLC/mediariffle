namespace MediaRiffle.Server.Media;

/// <summary>
/// The language a user wants subtitles in by default (Profile → Subtitles), and which track of a file that is. Files
/// name a track's language in whichever way their muxer did ("eng", "en", sometimes "English"), so each language
/// here lists the spellings that mean it.
/// </summary>
public static class SubtitleLanguages
{
    public sealed record Language(string Id, string Label, string[] Names);

    public static readonly IReadOnlyList<Language> All =
    [
        new("en", "English", ["en", "eng", "english"]),
        new("es", "Spanish", ["es", "spa", "spanish", "español"]),
        new("fr", "French", ["fr", "fre", "fra", "french", "français"]),
        new("de", "German", ["de", "ger", "deu", "german", "deutsch"]),
        new("it", "Italian", ["it", "ita", "italian", "italiano"]),
        new("pt", "Portuguese", ["pt", "por", "portuguese", "português"]),
        new("nl", "Dutch", ["nl", "dut", "nld", "dutch", "nederlands"]),
        new("ru", "Russian", ["ru", "rus", "russian"]),
        new("ja", "Japanese", ["ja", "jpn", "japanese"]),
        new("ko", "Korean", ["ko", "kor", "korean"]),
        new("zh", "Chinese", ["zh", "chi", "zho", "chinese"]),
    ];

    /// <summary>The id of a language the user may choose, or null for none (and for anything unknown).</summary>
    public static string? Normalize(string? id) =>
        All.FirstOrDefault(l => l.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// The subtitle track (as the player names it, "s{stream}") to turn on for <paramref name="languageId"/>, or null.
    /// Only a track the browser can show by itself: text, or PGS (drawn over the video). One that has to be burned
    /// into the video would turn a plain stream into a transcode, which isn't something to do unasked. Not a forced
    /// track (only the foreign lines of a film) or a commentary; text before PGS, and subtitles for the hearing
    /// impaired only when there are no others.
    /// </summary>
    public static string? Pick(IEnumerable<ProbeStream> subtitles, string? languageId)
    {
        var language = All.FirstOrDefault(l => l.Id == languageId);
        if (language is null) return null;
        var track = subtitles
            .Where(s => (s.IsTextSubtitle || s.IsPgs) && !s.IsForced && Matches(language, s) && !Has(s.Title, "commentary"))
            .OrderBy(s => Has(s.Title, "sdh") || Has(s.Title, "hearing") || Has(s.Title, "caption") ? 1 : 0)
            .ThenBy(s => s.IsTextSubtitle ? 0 : 1)
            .ThenBy(s => s.Index)
            .FirstOrDefault();
        return track is null ? null : $"s{track.Index}";
    }

    private static bool Matches(Language language, ProbeStream s) =>
        s.Language is { Length: > 0 } code && language.Names.Contains(code.Trim().ToLowerInvariant());

    private static bool Has(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}
