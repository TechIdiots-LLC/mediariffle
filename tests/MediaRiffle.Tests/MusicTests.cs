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
