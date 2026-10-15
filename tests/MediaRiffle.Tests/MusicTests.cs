using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.EntityFrameworkCore;
using Server = MediaRiffle.Server;

namespace MediaRiffle.Tests;

public class MusicTests
{
    [Theory]
    [InlineData("Pink Floyd/The Wall/01 - In the Flesh.flac", "Pink Floyd", "The Wall", null, 1, "In the Flesh", "Pink Floyd/The Wall")]
    [InlineData("Artist/Album/03. Song.mp3", "Artist", "Album", null, 3, "Song", "Artist/Album")]
    [InlineData("Artist/Album/2-05 Song Name.mp3", "Artist", "Album", 2, 5, "Song Name", "Artist/Album")]
    [InlineData("Artist/Album/CD2/07 - Song.mp3", "Artist", "Album", 2, 7, "Song", "Artist/Album")]
    [InlineData("Artist/Album/Song Without Number.ogg", "Artist", "Album", null, null, "Song Without Number", "Artist/Album")]
    [InlineData("Loose Album/Someone - 04 - Track.mp3", "Someone", "Loose Album", null, 4, "Track", "Loose Album")]
    [InlineData("single.mp3", null, null, null, null, "single", "")]
    public void TracksFromPaths(string path, string? artist, string? album, int? disc, int? track, string title, string folder)
    {
        var t = MediaNameParser.ParseTrack(path);
        Assert.Equal(artist, t.Artist);
        Assert.Equal(album, t.Album);
        Assert.Equal(disc, t.Disc);
        Assert.Equal(track, t.Track);
        Assert.Equal(title, t.Title);
        Assert.Equal(folder, t.Folder);
    }

    [Fact]
    public void A2001SongIsNotTrack200()
    {
        var t = MediaNameParser.ParseTrack("Artist/Album/2001 - A Space Odyssey.mp3");
        Assert.Null(t.Track);
        Assert.Equal("2001 - A Space Odyssey", t.Title);
    }

    [Fact]
    public void MusicLibrariesTakeAudioAndVideoLibrariesTakeVideo()
    {
        Assert.True(MediaNameParser.Belongs("a/b.flac", LibraryKind.Music));
        Assert.False(MediaNameParser.Belongs("a/b.mkv", LibraryKind.Music));
        Assert.True(MediaNameParser.Belongs("a/b.mkv", LibraryKind.Movies));
        Assert.False(MediaNameParser.Belongs("a/b.mp3", LibraryKind.Shows));
    }

    // Vorbis comments (FLAC, Ogg) are upper case and Ogg keeps them on the stream; ID3 and MP4 are lower case on the file
    [Fact]
    public void TagsAreReadInAnyCaseFromTheFileOrTheAudioStream()
    {
        var ogg = ProbeResult.Parse("""
            { "format": { "format_name": "ogg", "duration": "200.0" },
              "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "vorbis", "channels": 2,
                             "tags": { "TITLE": "Song", "ARTIST": "Band", "ALBUM": "Record", "TRACKNUMBER": "3", "DATE": "1999-05-01" } } ] }
            """);
        Assert.Equal("Song", ogg.Tag("title"));
        Assert.Equal("Band", ogg.Tag("album_artist", "albumartist", "artist"));
        Assert.Equal("3", ogg.Tag("track", "tracknumber"));

        var mp3 = ProbeResult.Parse("""
            { "format": { "format_name": "mp3", "tags": { "title": "From the file", "album_artist": "Various Artists", "track": "4/12" } },
              "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "mp3", "tags": { "title": "From the stream" } },
                           { "index": 1, "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } } ] }
            """);
        Assert.Equal("From the file", mp3.Tag("title"));
        Assert.Equal("Various Artists", mp3.Tag("album_artist"));
        // Cover art isn't a video
        Assert.Null(mp3.Video);
    }

    [Theory]
    [InlineData("mp3", "mp3", ".mp3", false, true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "aac", ".m4a", false, true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "alac", ".m4a", true, false)]   // Apple Lossless: converted
    [InlineData("flac", "flac", ".flac", true, true)]
    [InlineData("flac", "flac", ".flac", false, false)]                    // a browser without FLAC: converted
    [InlineData("asf", "wmav2", ".wma", true, false)]
    [InlineData("wav", "pcm_s16le", ".wav", false, true)]
    public void WhatPlaysAsItIs(string format, string codec, string ext, bool browserHasEverything, bool asIs)
    {
        var probe = ProbeResult.Parse($$"""
            { "format": { "format_name": "{{format}}" }, "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "{{codec}}" } ] }
            """);
        var caps = browserHasEverything ? new AudioCaps(true, true, true) : AudioCaps.Basic;
        Assert.Equal(asIs, AudioService.PlaysAsIs(probe, ext, caps));
    }

    // Filing tracks: tags win over folders, an album is one folder, and differing track artists make a compilation
    [Fact]
    public async Task TracksAreFiledIntoAlbumsAndArtists()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        await using var db = new Server.Data.AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Server.Data.AppDbContext>()
            .UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var dbLib = new Server.Data.MediaLibrary { Name = "Music", Kind = LibraryKind.Music, Path = "/music" };
        db.Libraries.Add(dbLib);
        await db.SaveChangesAsync();
        var lib = new LibraryInfo(dbLib.Id, "Music", LibraryKind.Music, "/music", [], true);

        async Task<Server.Data.MediaFile> File(string path, string tags)
        {
            var f = new Server.Data.MediaFile
            {
                LibraryId = lib.Id, RelativePath = path,
                ProbeJson = $$"""{ "format": { "format_name": "mp3", "tags": {{{tags}}} }, "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "mp3" } ] }""",
            };
            await LibraryScanner.FileTrackAsync(db, lib, f, default);
            db.MediaFiles.Add(f);
            await db.SaveChangesAsync();
            return f;
        }

        // Tagged: the tags decide, whatever the folders say
        var t1 = await File("misc/x.mp3", """ "title": "Come Together", "artist": "The Beatles", "album": "Abbey Road", "track": "1/17", "date": "1969" """);
        // Untagged: Artist/Album/NN - Title
        var t2 = await File("Nirvana/Nevermind/01 - Smells Like Teen Spirit.mp3", "");
        // A compilation without album artist tags: one album, its artist becomes Various Artists
        var c1 = await File("Now/a.mp3", """ "title": "One", "artist": "Singer A", "album": "Now 1" """);
        var c2 = await File("Now/b.mp3", """ "title": "Two", "artist": "Singer B", "album": "Now 1" """);

        string ArtistOf(Server.Data.MediaFile f) =>
            db.Artists.Single(a => a.Id == db.Albums.Single(x => x.Id == f.AlbumId).ArtistId).Name;

        Assert.Equal("Come Together", t1.Title);
        Assert.Equal(1, t1.TrackNumber);
        Assert.Equal("The Beatles", ArtistOf(t1));
        Assert.Equal(1969, db.Albums.Single(a => a.Id == t1.AlbumId).Year);

        Assert.Equal("Smells Like Teen Spirit", t2.Title);
        Assert.Equal("Nirvana", ArtistOf(t2));
        Assert.Equal("Nevermind", db.Albums.Single(a => a.Id == t2.AlbumId).Title);

        Assert.Equal(c1.AlbumId, c2.AlbumId);
        Assert.Equal(Server.Data.Artist.Various, ArtistOf(c2));
        Assert.Equal("Singer B", c2.TrackArtist);
    }

    // Hiding an artist hides all their albums' tracks; an album, its tracks; lists hold artists, albums and single tracks
    [Fact]
    public async Task MusicCanBeHiddenAndListed()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        await using var db = new Server.Data.AppDbContext(new DbContextOptionsBuilder<Server.Data.AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new Server.Data.AppUser { Id = "me", UserName = "me" });
        var lib = new Server.Data.MediaLibrary { Name = "Music", Kind = LibraryKind.Music, Path = "/music" };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var a1 = new Server.Data.Artist { LibraryId = lib.Id, Name = "A" };
        var a2 = new Server.Data.Artist { LibraryId = lib.Id, Name = "B" };
        db.AddRange(a1, a2);
        await db.SaveChangesAsync();
        var x1 = new Server.Data.Album { LibraryId = lib.Id, ArtistId = a1.Id, Title = "A1", GroupKey = "a1" };
        var x2 = new Server.Data.Album { LibraryId = lib.Id, ArtistId = a2.Id, Title = "B1", GroupKey = "b1" };
        var x3 = new Server.Data.Album { LibraryId = lib.Id, ArtistId = a2.Id, Title = "B2", GroupKey = "b2" };
        db.AddRange(x1, x2, x3);
        await db.SaveChangesAsync();
        Server.Data.MediaFile T(string p, int album) => new()
            { LibraryId = lib.Id, RelativePath = p, Kind = Server.Data.MediaKind.Track, AlbumId = album, Title = p };
        var t1 = T("a1/1", x1.Id); var t2 = T("b1/1", x2.Id); var t3 = T("b2/1", x3.Id); var t4 = T("b2/2", x3.Id);
        db.MediaFiles.AddRange(t1, t2, t3, t4);
        await db.SaveChangesAsync();

        db.Hidden.AddRange(
            new Server.Data.HiddenItem { UserId = "me", ArtistId = a1.Id },
            new Server.Data.HiddenItem { UserId = "me", AlbumId = x2.Id },
            new Server.Data.HiddenItem { UserId = "me", MediaFileId = t4.Id });
        db.UserTags.AddRange(
            new Server.Data.UserTag { UserId = "me", Tag = "Mix", ArtistId = a1.Id },
            new Server.Data.UserTag { UserId = "me", Tag = "Mix", AlbumId = x2.Id },
            new Server.Data.UserTag { UserId = "me", Tag = "Mix", MediaFileId = t4.Id });
        await db.SaveChangesAsync();

        var visible = await Server.Api.HiddenEndpoints.NotHidden(db, "me", db.MediaFiles).Select(f => f.Id).ToListAsync();
        Assert.Equal([t3.Id], visible);

        var mix = db.UserTags.Where(t => t.UserId == "me" && t.Tag == "Mix");
        var inMix = await Server.Api.ListEndpoints.FilesIn(db, mix, db.MediaFiles).Select(f => f.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { t1.Id, t2.Id, t4.Id }.Order(), inMix);

        var hidden = await Server.Api.HiddenEndpoints.ListAsync(db, "me");
        Assert.Equal(new[] { "album", "artist", "track" }, hidden.Select(x => x.Kind).Order());
        Assert.Contains(hidden, x => x.Kind == "album" && x.Title == "B1" && x.Detail == "B");
        Assert.Contains(hidden, x => x.Kind == "track" && x.Id == t4.Id && x.Detail == "B2");
    }

    // A Chromecast's link opens its own track, while it's in date and its user exists; an altered one opens nothing
    [Fact]
    public async Task CastLinksOpenOnlyTheirTrackWhileValid()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        await using var db = new Server.Data.AppDbContext(new DbContextOptionsBuilder<Server.Data.AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var lib = new Server.Data.MediaLibrary { Name = "Music", Kind = LibraryKind.Music, Path = "/music" };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var track = new Server.Data.MediaFile { LibraryId = lib.Id, RelativePath = "a.mp3", Kind = Server.Data.MediaKind.Track };
        db.MediaFiles.Add(track);
        await db.SaveChangesAsync();

        var keys = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        Func<string, Task<bool>> exists = id => Task.FromResult(id == "me");
        var token = Server.Api.MusicCastEndpoints.Token(keys, track.Id, "me", DateTimeOffset.UtcNow.AddHours(1));
        string Altered(string t) => t[..^4] + (t[^4] == 'A' ? 'B' : 'A') + t[^3..];

        Assert.Equal(track.Id, (await Server.Api.MusicCastEndpoints.ResolveAsync(token, keys, exists, db))?.Id);
        Assert.Null(await Server.Api.MusicCastEndpoints.ResolveAsync(Altered(token), keys, exists, db));
        Assert.Null(await Server.Api.MusicCastEndpoints.ResolveAsync("not-a-token", keys, exists, db));
        // A removed user's links stop working
        var theirs = Server.Api.MusicCastEndpoints.Token(keys, track.Id, "gone", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(await Server.Api.MusicCastEndpoints.ResolveAsync(theirs, keys, exists, db));
        // So do expired ones
        var old = Server.Api.MusicCastEndpoints.Token(keys, track.Id, "me", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await Server.Api.MusicCastEndpoints.ResolveAsync(old, keys, exists, db));
        // Chromecasts play FLAC and Ogg as they are
        Assert.True(Server.Api.MusicCastEndpoints.Chromecast is { Flac: true, Ogg: true, Opus: true });
    }

    [Fact]
    public void CoverArtIsNoticedAndExtractedSmall()
    {
        var withCover = ProbeResult.Parse("""
            { "format": { "format_name": "mp3" }, "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "mp3" },
              { "index": 1, "codec_type": "video", "codec_name": "png", "disposition": { "attached_pic": 1 } } ] }
            """);
        Assert.True(withCover.HasCoverArt);
        Assert.False(ProbeResult.Parse("""{ "format": {}, "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "mp3" } ] }""").HasCoverArt);
        var args = ImageService.CoverArguments("in.mp3", "out.jpg");
        Assert.Contains("0:v:0", args);
        Assert.Equal("out.jpg", args[^1]);
    }

    [Fact]
    public void ConversionKeepsOnlyTheSoundAndCanSeek()
    {
        var a = AudioService.Arguments("in.wma", "out.m4a");
        Assert.Contains("0:a:0", a);
        Assert.Contains("aac", a);
        Assert.Contains("+faststart", a);
        Assert.Equal("out.m4a", a[^1]);
    }
}
