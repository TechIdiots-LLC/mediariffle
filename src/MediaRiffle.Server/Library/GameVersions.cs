using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaRiffle.Server.Library;

/// <summary>
/// The versions of one game: a set often has a title several times over (regions, revisions, bad dumps, hacks,
/// translations: "Zelda (U) (V1.0) [!]", "Zelda (G) [b1]", "Zelda (U) [T+Spa]"). They're shown as one game, under the
/// name without its tags, played as the best of them unless another is picked on the game's page.
/// </summary>
public static partial class GameVersions
{
    private static readonly ConcurrentDictionary<string, string> Keys = new(StringComparer.Ordinal);

    /// <summary>
    /// What a game's versions have in common (the box art's comparison: letters and digits, without tags, DS sets'
    /// numbers or "The"). Empty when a name is nothing but tags: such a game is a title of its own.
    /// </summary>
    public static string Key(string title) => Keys.GetOrAdd(title, Games.GameArt.Key);

    /// <summary>The game's name without its tags or DS set number: "2333 Kung Fu Panda (US)" is "Kung Fu Panda".</summary>
    public static string Name(string title)
    {
        var s = NumberedSet().Replace(title, "");
        s = Spaces().Replace(Tags().Replace(s, " "), " ").Trim().TrimEnd('-', ',', ' ');
        return s.Length > 0 ? s : title;
    }

    /// <summary>
    /// Versions best first: a good dump before a bad one, a hack, a translation, a trainer or a beta; then USA, World,
    /// Europe, untagged, Japan; a verified dump ([!]); the later revision; the fewest tags. By file name, which keeps
    /// the "[!]" the title leaves out.
    /// </summary>
    public static List<T> Best<T>(IEnumerable<T> versions, Func<T, string> fileName) =>
        versions.Select(v => (v, name: Path.GetFileNameWithoutExtension(fileName(v))))
            .OrderBy(x => Flaws(x.name))
            .ThenBy(x => RegionOrder(x.name))
            .ThenBy(x => x.name.Contains("[!]") ? 0 : 1)
            .ThenByDescending(x => Revision(x.name))
            .ThenBy(x => Tags().Matches(x.name).Count)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.v)
            .ToList();

    /// <summary>How far from the plain game: 2 a broken dump, 1 a changed or unfinished one, 0 neither.</summary>
    private static int Flaws(string name)
    {
        var worst = 0;
        foreach (Match m in Tags().Matches(name))
        {
            var tag = m.Value[1..^1].Trim();
            if (m.Value[0] == '[')
            {
                if (BrokenTag().IsMatch(tag)) return 2;
                if (ChangedTag().IsMatch(tag)) worst = 1;
            }
            else if (Unfinished.Any(u => tag.StartsWith(u, StringComparison.OrdinalIgnoreCase))) worst = 1;
        }
        return worst;
    }

    private static readonly string[] Unfinished = ["beta", "proto", "demo", "sample", "kiosk", "pirate", "unl", "hack"];

    private static int RegionOrder(string name) => Games.GameArt.Region(name) switch
    {
        "usa" => 0, "world" => 1, "europe" => 2, null => 3, _ => 4,
    };

    private static double Revision(string name)
    {
        var m = RevisionTag().Match(name);
        if (!m.Success) return 0;
        var v = m.Groups[1].Value;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
            : v.Length == 1 && char.IsAsciiLetter(v[0]) ? char.ToUpperInvariant(v[0]) - 'A' + 1 : 0;
    }

    /// <summary>
    /// What tells each version apart, in words: "USA · v1.1 · verified", "Germany · bad dump", "USA · Spanish
    /// translation". Two that would read the same get their file names after.
    /// </summary>
    public static List<string> Labels(IReadOnlyList<string> fileNames)
    {
        var labels = fileNames.Select(f => Label(Path.GetFileNameWithoutExtension(f))).ToList();
        var twice = labels.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Count; i++)
            if (labels[i].Length == 0) labels[i] = fileNames[i];
            else if (twice.Contains(labels[i])) labels[i] += " — " + fileNames[i];
        return labels;
    }

    /// <summary>One version's tags in words ("" when it has none).</summary>
    public static string Label(string name)
    {
        var words = new List<string>();
        foreach (Match m in Tags().Matches(NumberedSet().Replace(name, "")))
        {
            var tag = m.Value[1..^1].Trim();
            var word = m.Value[0] == '[' ? BracketWord(tag) : ParenWord(tag);
            if (!string.IsNullOrEmpty(word) && !words.Contains(word, StringComparer.OrdinalIgnoreCase)) words.Add(word);
        }
        return string.Join(" · ", words);
    }

    // GoodTools' country codes and the DS sets' two-letter ones
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["U"] = "USA", ["US"] = "USA", ["USA"] = "USA", ["E"] = "Europe", ["EU"] = "Europe", ["EUR"] = "Europe",
        ["J"] = "Japan", ["JP"] = "Japan", ["JPN"] = "Japan", ["W"] = "World", ["UE"] = "USA, Europe", ["EU-US"] = "USA, Europe",
        ["JU"] = "Japan, USA", ["UJ"] = "Japan, USA", ["JE"] = "Japan, Europe", ["JUE"] = "World",
        ["F"] = "France", ["FR"] = "France", ["G"] = "Germany", ["DE"] = "Germany", ["GER"] = "Germany",
        ["S"] = "Spain", ["ES"] = "Spain", ["SPA"] = "Spain", ["I"] = "Italy", ["IT"] = "Italy", ["ITA"] = "Italy",
        ["K"] = "Korea", ["KR"] = "Korea", ["KS"] = "Korea", ["KOR"] = "Korea", ["C"] = "China", ["CH"] = "China", ["CN"] = "China",
        ["NL"] = "Netherlands", ["H"] = "Netherlands", ["HOL"] = "Netherlands", ["A"] = "Australia", ["AU"] = "Australia", ["AUS"] = "Australia",
        ["B"] = "Brazil", ["BR"] = "Brazil", ["SW"] = "Sweden", ["SWE"] = "Sweden", ["SE"] = "Sweden", ["DK"] = "Denmark", ["NO"] = "Norway",
        ["UK"] = "UK", ["ENG"] = "English", ["PD"] = "public domain", ["UNL"] = "unlicensed",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Eng"] = "English", ["Spa"] = "Spanish", ["Esp"] = "Spanish", ["Fre"] = "French", ["Fra"] = "French", ["Ger"] = "German",
        ["Deu"] = "German", ["Ita"] = "Italian", ["Por"] = "Portuguese", ["Bra"] = "Brazilian Portuguese", ["PorBR"] = "Brazilian Portuguese",
        ["Rus"] = "Russian", ["Chi"] = "Chinese", ["Kor"] = "Korean", ["Jap"] = "Japanese", ["Swe"] = "Swedish", ["Nor"] = "Norwegian",
        ["Dan"] = "Danish", ["Fin"] = "Finnish", ["Dut"] = "Dutch", ["Pol"] = "Polish", ["Hun"] = "Hungarian", ["Gre"] = "Greek",
        ["Tur"] = "Turkish", ["Cat"] = "Catalan", ["Heb"] = "Hebrew", ["Ara"] = "Arabic", ["Cze"] = "Czech", ["Rom"] = "Romanian",
    };

    private static string ParenWord(string tag)
    {
        if (Countries.TryGetValue(tag, out var country)) return country;
        if (MultiLanguage().Match(tag) is { Success: true } ml) return $"{ml.Groups[1].Value} languages";
        if (RevisionTag().Match($"({tag})") is { Success: true } rev)
            return tag.StartsWith('V') || tag.StartsWith('v') ? "v" + rev.Groups[1].Value : "Rev " + rev.Groups[1].Value;
        return tag;   // No-Intro's words ("USA", "Europe", "En,Fr,De", "Beta") and anything else, as written
    }

    private static string? BracketWord(string tag)
    {
        if (tag == "!") return "verified";
        if (tag is "C" or "S" or "M") return null;   // works on Game Boy Color, Super Game Boy: the same in every version
        // "T+Spa1.0_DJTraducciones": a Spanish translation, its version 1.0, by DJTraducciones
        if (TranslationTag().Match(tag) is { Success: true } t)
        {
            var lang = Languages.GetValueOrDefault(t.Groups[2].Value, t.Groups[2].Value);
            var rest = TranslationRest().Match(t.Groups[3].Value);
            var version = rest.Groups[1].Value.Trim('.');
            var by = rest.Groups[2].Value.Replace('_', ' ').Trim();
            return $"{lang} translation" + (version.Length > 0 ? " " + version : "") + (by.Length > 0 ? " by " + by : "")
                + (t.Groups[1].Value == "-" ? " (unfinished)" : "");
        }
        if (tag.StartsWith("BF", StringComparison.Ordinal)) return "bung fix";
        return tag[0] switch
        {
            'a' when Numbered(tag) => "alternate dump",
            'b' when Numbered(tag) => "bad dump",
            'o' when Numbered(tag) => "overdump",
            'h' when Numbered(tag) => "hack",
            't' when Numbered(tag) => "trainer",
            'f' when Numbered(tag) => "fixed",
            'p' when Numbered(tag) => "pirate",
            'x' when Numbered(tag) => "bad checksum",
            _ => tag,
        };
    }

    // "b", "b1", "h2C", "a10": GoodTools' letter with its number and what may follow
    private static bool Numbered(string tag) => tag.Length == 1 || char.IsAsciiDigit(tag[1]);

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^\s*(\d{4}|x{4})\s+", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedSet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Bad dump, overdump, bad checksum: [b1], [o2], [x]
    [GeneratedRegex(@"^[box]\d*$")]
    private static partial Regex BrokenTag();

    // Hack, trainer, translation, fixed, pirate, alternate: [h1C], [t1], [T+Spa], [f1], [p1], [a1]
    [GeneratedRegex(@"^([hftpa]\d|[hftpa]$|T[+-])")]
    private static partial Regex ChangedTag();

    [GeneratedRegex(@"^T([+-])([A-Za-z]{2,5}?)(?=[\d._ ]|$|[A-Z][a-z])(.*)$")]
    private static partial Regex TranslationTag();

    // After the language: the translation's version, then who made it
    [GeneratedRegex(@"^([\d.]*)[_ ]?(.*)$")]
    private static partial Regex TranslationRest();

    [GeneratedRegex(@"\((?:V\s?|Rev\s?|REV)([\d.]+|[A-Z])\)", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionTag();

    [GeneratedRegex(@"^M(\d+)$")]
    private static partial Regex MultiLanguage();
}
