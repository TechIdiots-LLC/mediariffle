using System.Globalization;
using System.Text;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Lists and categories as a CSV file: a row per show, movie, artist, album, song or game, by name rather than number,
/// so the file can be edited in a spreadsheet and still finds the same things (here, or on a server with the same
/// media). Importing adds the file's rows to each list it names, or (replace) makes each of those lists what the file
/// says, in its order; lists it doesn't name are left alone.
/// </summary>
public static class ListFile
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxUnmatchedShown = 200;

    public const string Show = "Show", Movie = "Movie", Artist = "Artist", Album = "Album", Song = "Song", Game = "Game";

    public sealed record Row(string List, string Kind, string Title, int? Year = null, string? Artist = null, string? Album = null,
        string? System = null, string? Library = null, string? File = null);

    /// <summary>What an entry points at: exactly one id.</summary>
    public sealed record Entry(int? ShowId = null, int? MovieId = null, int? ArtistId = null, int? AlbumId = null,
        int? MediaFileId = null, int? GameId = null) : ITagged
    {
        public static Entry Of(ITagged t) => new(t.ShowId, t.MovieId, t.ArtistId, t.AlbumId, t.MediaFileId, t.GameId);
    }

    public sealed record Unmatched(int Line, string List, string Kind, string Title, string Why);

    /// <summary>The lists a file names, each with what was found for it in file order, and the rows that found nothing.</summary>
    public sealed class Plan
    {
        public List<(string Name, List<Entry> Entries)> Lists { get; } = [];
        public List<Unmatched> Unmatched { get; } = [];
        /// <summary>All the rows that found nothing (only the first few are in <see cref="Unmatched"/>).</summary>
        public int UnmatchedCount { get; set; }
    }

    public sealed record Result(int Lists, int Added, int Removed, int Kept, int UnmatchedCount, IReadOnlyList<Unmatched> Unmatched);

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>A row for each entry (one whose show, movie… is gone has none), in the order given.</summary>
    public static async Task<List<Row>> RowsAsync(AppDbContext db, IReadOnlyList<(string Tag, ITagged Item)> entries)
    {
        HashSet<int> Ids(Func<ITagged, int?> id) => entries.Select(e => id(e.Item)).OfType<int>().ToHashSet();
        var showIds = Ids(t => t.ShowId);
        var movieIds = Ids(t => t.MovieId);
        var trackIds = Ids(t => t.MediaFileId);
        var gameIds = Ids(t => t.GameId);

        var libraries = await db.Libraries.ToDictionaryAsync(l => l.Id, l => l.Name);
        var shows = await db.Shows.Where(s => showIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var movies = await db.Movies.Where(m => movieIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        var movieFiles = (await db.MediaFiles.Where(f => f.MovieId != null && movieIds.Contains(f.MovieId.Value))
                .Select(f => new { MovieId = f.MovieId!.Value, f.PartNumber, f.RelativePath }).ToListAsync())
            .GroupBy(f => f.MovieId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.PartNumber).First().RelativePath);
        var tracks = await db.MediaFiles.Where(f => trackIds.Contains(f.Id))
            .Select(f => new { f.Id, f.LibraryId, f.Title, f.TrackArtist, f.AlbumId, f.RelativePath }).ToDictionaryAsync(f => f.Id);
        var albumIds = Ids(t => t.AlbumId).Concat(tracks.Values.Select(t => t.AlbumId).OfType<int>()).ToHashSet();
        var albums = await db.Albums.Where(a => albumIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        var artistIds = Ids(t => t.ArtistId).Concat(albums.Values.Select(a => a.ArtistId)).ToHashSet();
        var artists = await db.Artists.Where(a => artistIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        var games = await db.Games.Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id);

        string? Lib(int id) => libraries.GetValueOrDefault(id);
        string? ArtistName(int id) => artists.GetValueOrDefault(id)?.Name;
        var rows = new List<Row>();
        foreach (var (tag, t) in entries)
        {
            Row? row = t switch
            {
                { ShowId: { } id } when shows.TryGetValue(id, out var s) => new Row(tag, Show, s.Title, Library: Lib(s.LibraryId), File: s.Folder),
                { MovieId: { } id } when movies.TryGetValue(id, out var m) =>
                    new Row(tag, Movie, m.Title, m.Year, Library: Lib(m.LibraryId), File: movieFiles.GetValueOrDefault(id)),
                { ArtistId: { } id } when artists.TryGetValue(id, out var a) => new Row(tag, Artist, a.Name, Library: Lib(a.LibraryId)),
                { AlbumId: { } id } when albums.TryGetValue(id, out var a) =>
                    new Row(tag, Album, a.Title, a.Year, ArtistName(a.ArtistId), Library: Lib(a.LibraryId), File: a.Folder),
                { MediaFileId: { } id } when tracks.TryGetValue(id, out var f) => new Row(tag, Song, f.Title,
                    Artist: f.TrackArtist ?? (f.AlbumId is { } al && albums.TryGetValue(al, out var a) ? ArtistName(a.ArtistId) : null),
                    Album: f.AlbumId is { } b && albums.TryGetValue(b, out var album) ? album.Title : null,
                    Library: Lib(f.LibraryId), File: f.RelativePath),
                { GameId: { } id } when games.TryGetValue(id, out var g) =>
                    new Row(tag, Game, g.Title, System: g.System, Library: Lib(g.LibraryId), File: g.RelativePath),
                _ => null,
            };
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    /// <summary>The rows as CSV (UTF-8 with a byte order mark, which spreadsheets need to read it as UTF-8).</summary>
    public static byte[] ToCsv(string firstColumn, IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        Line(sb, [firstColumn, "Kind", "Title", "Year", "Artist", "Album", "System", "Library", "File"]);
        foreach (var r in rows)
            Line(sb, [r.List, r.Kind, r.Title, r.Year?.ToString(CultureInfo.InvariantCulture), r.Artist, r.Album, r.System, r.Library, r.File]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void Line(StringBuilder sb, IEnumerable<string?> fields) =>
        sb.AppendJoin(',', fields.Select(Quote)).Append("\r\n");

    private static string Quote(string? v) =>
        v is null ? "" : v.IndexOfAny([',', '"', '\r', '\n', ';', '\t']) >= 0 || v != v.Trim() ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>A name for the downloaded file: "mediariffle-<what>.csv", with only letters, digits and dashes.</summary>
    public static string FileName(string what)
    {
        var safe = new string(what.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        safe = string.Join('-', safe.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return $"mediariffle-{(safe.Length == 0 ? "list" : safe[..Math.Min(safe.Length, 60)])}.csv";
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The uploaded file's text: UTF-8 or UTF-16 (by its byte order mark), or, when it isn't valid UTF-8, Windows-1252
    /// (what a spreadsheet's plain "CSV" saves on Windows). An error when it's too big.
    /// </summary>
    public static async Task<(string? Text, string? Error)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        const string tooBig = "That file is too big for a list file (at most 4 MB).";
        if (request.ContentLength > MaxBytes) return (null, tooBig);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes) return (null, tooBig);
            buffer.Write(chunk, 0, n);
        }
        return (Decode(buffer.ToArray()), null);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var skip = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { return Windows1252().GetString(bytes); }
    }

    private static Encoding Windows1252()
    {
        try { return CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1; }
        catch (Exception) { return Encoding.Latin1; }
    }

    /// <summary>
    /// The file's rows, each with the line it starts on. The first line names the columns, in any order: List (or
    /// Category), Kind and Title are needed, and Year, Artist, Album, System, Library and File help find the right one.
    /// Commas, semicolons or tabs between fields, whichever the first line uses.
    /// </summary>
    public static (List<(int Line, Row Row)>? Rows, string? Error) Parse(string text)
    {
        var records = SplitCsv(text);
        if (records.Count == 0) return (null, "The file is empty.");
        var header = records[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(names.Contains);
        int list = Col("list", "category"), kind = Col("kind", "type"), title = Col("title", "name"),
            year = Col("year"), artist = Col("artist"), album = Col("album"), system = Col("system"), library = Col("library"), file = Col("file", "path");
        if (list < 0 || kind < 0 || title < 0)
            return (null, "The file's first line should name its columns, with List, Kind and Title among them (as an exported file has).");

        var rows = new List<(int, Row)>();
        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            string Get(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";
            string? Opt(int i) => Get(i) is { Length: > 0 } v ? v : null;
            rows.Add((line, new Row(Get(list), Get(kind), Get(title),
                int.TryParse(Get(year), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null,
                Opt(artist), Opt(album), Opt(system), Opt(library), Opt(file))));
        }
        return (rows, null);
    }

    private static List<(int Line, List<string> Fields)> SplitCsv(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var sep = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        int line = 1, start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') { if (c == '\n') line++; field.Append(c); }
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"' && !wasQuoted && string.IsNullOrWhiteSpace(field.ToString())) { field.Clear(); quoted = wasQuoted = true; }
            else if (c == sep) { fields.Add(field.ToString()); field.Clear(); wasQuoted = false; }
            else if (c == '\n')
            {
                fields.Add(field.ToString()); field.Clear(); wasQuoted = false;
                records.Add((start, fields)); fields = [];
                start = ++line;
            }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString()); records.Add((start, fields)); }
        return records;
    }

    private static string? KindOf(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "show" or "shows" or "series" or "tv" => Show,
        "movie" or "movies" or "film" => Movie,
        "artist" or "artists" => Artist,
        "album" or "albums" => Album,
        "song" or "songs" or "track" or "tracks" => Song,
        "game" or "games" => Game,
        _ => null,
    };

    /// <summary>Something a row can be: what it is, and what else tells it apart from others of the same title.</summary>
    private sealed record Candidate(Entry Entry, string Title, int LibraryId, string? File = null, int? Year = null,
        string? Artist = null, string? AlbumArtist = null, string? Album = null, string? System = null);

    /// <summary>Titles and paths compared without case, spaces run together, and \ the same as /.</summary>
    private static string Key(string s) =>
        string.Join(' ', s.Replace('\\', '/').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool Same(string? a, string? b) => a is not null && b is not null && Key(a) == Key(b);

    /// <summary>
    /// What each row is in the enabled libraries: by its kind and title, kept to those with the year, artist, album or
    /// system it gives (a movie or album with no year matches any); by its file when no title matches. Several left (the
    /// same show in two libraries), its library and then its file pick among them, and failing that the row has them all.
    /// </summary>
    /// <param name="name">The list's name as stored, or why it can't be one.</param>
    public static async Task<Plan> ResolveAsync(AppDbContext db, IReadOnlyList<(int Line, Row Row)> rows, IReadOnlyCollection<LibraryInfo> enabled,
        Func<string, (string? Name, string? Error)> name)
    {
        var ids = enabled.Select(l => l.Id).ToHashSet();
        var libraryName = enabled.ToDictionary(l => l.Id, l => l.Name);
        var kinds = rows.Select(r => KindOf(r.Row.Kind)).OfType<string>().ToHashSet();
        var all = new Dictionary<string, List<Candidate>>();
        foreach (var kind in kinds)
            all[kind] = kind switch
            {
                Show => await db.Shows.Where(s => ids.Contains(s.LibraryId))
                    .Select(s => new Candidate(new Entry(s.Id, null, null, null, null, null), s.Title, s.LibraryId, s.Folder, null, null, null, null, null)).ToListAsync(),
                Movie => await MoviesAsync(db, ids),
                Artist => await db.Artists.Where(a => ids.Contains(a.LibraryId))
                    .Select(a => new Candidate(new Entry(null, null, a.Id, null, null, null), a.Name, a.LibraryId, null, null, null, null, null, null)).ToListAsync(),
                Album => await db.Albums.Where(a => ids.Contains(a.LibraryId))
                    .Join(db.Artists, a => a.ArtistId, r => r.Id, (a, r) => new Candidate(new Entry(null, null, null, a.Id, null, null),
                        a.Title, a.LibraryId, a.Folder, a.Year, r.Name, null, null, null)).ToListAsync(),
                Song => await SongsAsync(db, ids),
                _ => await db.Games.Where(g => ids.Contains(g.LibraryId))
                    .Select(g => new Candidate(new Entry(null, null, null, null, null, g.Id), g.Title, g.LibraryId, g.RelativePath, null, null, null, null, g.System)).ToListAsync(),
            };
        var byTitle = all.ToDictionary(k => k.Key, k => k.Value.ToLookup(c => Key(c.Title)));
        var byFile = all.ToDictionary(k => k.Key, k => k.Value.Where(c => c.File is not null).ToLookup(c => Key(c.File!)));

        var plan = new Plan();
        var lists = new Dictionary<string, (List<Entry> Entries, HashSet<Entry> Seen)>(StringComparer.OrdinalIgnoreCase);
        void Miss(int line, Row r, string why)
        {
            if (plan.UnmatchedCount++ < MaxUnmatchedShown) plan.Unmatched.Add(new Unmatched(line, r.List, r.Kind, r.Title, why));
        }
        foreach (var (line, r) in rows)
        {
            var (list, error) = name(r.List);
            if (list is null) { Miss(line, r, error ?? "No list name."); continue; }
            if (KindOf(r.Kind) is not { } kind) { Miss(line, r, "Kind should be Show, Movie, Artist, Album, Song or Game."); continue; }
            if (r.Title.Length == 0 && r.File is null) { Miss(line, r, "No title."); continue; }

            var found = byTitle[kind][Key(r.Title)].Where(c =>
                (r.Year is null || c.Year is null || c.Year == r.Year) &&
                (r.Artist is null || kind is not (Album or Song) || Same(c.Artist, r.Artist) || Same(c.AlbumArtist, r.Artist)) &&
                (r.Album is null || kind != Song || Same(c.Album, r.Album)) &&
                (r.System is null || kind != Game || Same(c.System, r.System))).ToList();
            if (found.Count == 0 && r.File is not null) found = byFile[kind][Key(r.File)].ToList();
            if (found.Count == 0) { Miss(line, r, "Not found in the libraries."); continue; }
            if (found.Count > 1 && r.Library is not null && found.Where(c => Same(libraryName[c.LibraryId], r.Library)).ToList() is { Count: > 0 } inLibrary)
                found = inLibrary;
            if (found.Count > 1 && r.File is not null && found.Where(c => Same(c.File, r.File)).ToList() is { Count: > 0 } atFile)
                found = atFile;

            if (!lists.TryGetValue(list, out var entries))
            {
                lists[list] = entries = ([], []);
                plan.Lists.Add((list, entries.Entries));
            }
            foreach (var c in found) if (entries.Seen.Add(c.Entry)) entries.Entries.Add(c.Entry);
        }
        return plan;
    }

    private static async Task<List<Candidate>> MoviesAsync(AppDbContext db, HashSet<int> ids)
    {
        var files = (await db.MediaFiles.Where(f => f.MovieId != null && ids.Contains(f.LibraryId))
                .Select(f => new { MovieId = f.MovieId!.Value, f.PartNumber, f.RelativePath }).ToListAsync())
            .GroupBy(f => f.MovieId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.PartNumber).First().RelativePath);
        return (await db.Movies.Where(m => ids.Contains(m.LibraryId)).Select(m => new { m.Id, m.Title, m.LibraryId, m.Year }).ToListAsync())
            .Select(m => new Candidate(new Entry(MovieId: m.Id), m.Title, m.LibraryId, files.GetValueOrDefault(m.Id), m.Year)).ToList();
    }

    private static async Task<List<Candidate>> SongsAsync(AppDbContext db, HashSet<int> ids)
    {
        var albums = await db.Albums.Where(a => ids.Contains(a.LibraryId))
            .Join(db.Artists, a => a.ArtistId, r => r.Id, (a, r) => new { a.Id, a.Title, Artist = r.Name }).ToDictionaryAsync(a => a.Id);
        return (await db.MediaFiles.Where(f => f.Kind == MediaKind.Track && ids.Contains(f.LibraryId))
                .Select(f => new { f.Id, f.Title, f.LibraryId, f.RelativePath, f.TrackArtist, f.AlbumId }).ToListAsync())
            .Select(f =>
            {
                var album = f.AlbumId is { } a ? albums.GetValueOrDefault(a) : null;
                return new Candidate(new Entry(MediaFileId: f.Id), f.Title, f.LibraryId, f.RelativePath,
                    Artist: f.TrackArtist ?? album?.Artist, AlbumArtist: album?.Artist, Album: album?.Title);
            }).ToList();
    }

    /// <summary>
    /// The plan made: each list it names gets what it found that isn't on it (the list's spelling kept); with
    /// <paramref name="replace"/>, what isn't in the file comes off it (unless it's in a library that's switched off,
    /// which the file can't name) and the list takes the file's order. Nothing is saved: the caller saves, or doesn't
    /// for a preview.
    /// </summary>
    /// <param name="existing">Every entry there is now (the user's, or all categories'), tracked by <paramref name="db"/>.</param>
    public static async Task<Result> ApplyAsync<T>(AppDbContext db, IReadOnlyList<T> existing, Plan plan, bool replace,
        IReadOnlySet<int> enabled, Func<string, Entry, DateTime, T> make) where T : class, IListEntry
    {
        var named = plan.Lists.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touched = existing.Where(e => named.Contains(e.Tag)).ToList();
        var visible = replace ? await InLibrariesAsync(db, touched.Select(Entry.Of), enabled) : [];
        var now = DateTime.UtcNow;
        var step = 0;
        int added = 0, removed = 0, kept = 0;
        foreach (var (name, wanted) in plan.Lists)
        {
            var have = touched.Where(e => string.Equals(e.Tag, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var spelling = have.FirstOrDefault()?.Tag ?? name;
            var byEntry = have.GroupBy(Entry.Of).ToDictionary(g => g.Key, g => g.First());
            foreach (var w in wanted)
            {
                var when = now.AddMilliseconds(step++);
                if (byEntry.TryGetValue(w, out var same))
                {
                    if (replace) same.CreatedUtc = when;
                    kept++;
                }
                else { db.Add(make(spelling, w, when)); added++; }
            }
            if (!replace) continue;
            var keep = wanted.ToHashSet();
            foreach (var e in have)
            {
                var entry = Entry.Of(e);
                // A second copy of an entry goes too
                if ((!keep.Contains(entry) || !ReferenceEquals(byEntry[entry], e)) && visible.Contains(entry)) { db.Remove(e); removed++; }
            }
        }
        return new Result(plan.Lists.Count, added, removed, kept, plan.UnmatchedCount, plan.Unmatched);
    }

    /// <summary>The entries whose show, movie, artist, album, song or game is in one of the enabled libraries.</summary>
    private static async Task<HashSet<Entry>> InLibrariesAsync(AppDbContext db, IEnumerable<Entry> entries, IReadOnlySet<int> enabled)
    {
        var list = entries.ToList();
        HashSet<int> Ids(Func<Entry, int?> id) => list.Select(id).OfType<int>().ToHashSet();
        var (shows, movies, artists, albums, files, games) = (Ids(e => e.ShowId), Ids(e => e.MovieId), Ids(e => e.ArtistId),
            Ids(e => e.AlbumId), Ids(e => e.MediaFileId), Ids(e => e.GameId));
        var found = new HashSet<Entry>();
        found.UnionWith((await db.Shows.Where(s => shows.Contains(s.Id)).Select(s => new { s.Id, s.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(ShowId: x.Id)));
        found.UnionWith((await db.Movies.Where(m => movies.Contains(m.Id)).Select(m => new { m.Id, m.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(MovieId: x.Id)));
        found.UnionWith((await db.Artists.Where(a => artists.Contains(a.Id)).Select(a => new { a.Id, a.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(ArtistId: x.Id)));
        found.UnionWith((await db.Albums.Where(a => albums.Contains(a.Id)).Select(a => new { a.Id, a.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(AlbumId: x.Id)));
        found.UnionWith((await db.MediaFiles.Where(f => files.Contains(f.Id)).Select(f => new { f.Id, f.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(MediaFileId: x.Id)));
        found.UnionWith((await db.Games.Where(g => games.Contains(g.Id)).Select(g => new { g.Id, g.LibraryId }).ToListAsync())
            .Where(x => enabled.Contains(x.LibraryId)).Select(x => new Entry(GameId: x.Id)));
        return found;
    }
}
