using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Streaming;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <param name="Subtitle">"s{stream}" for one in the file, "x{n}" for a side-car file, or null.</param>
/// <param name="Burn">Burn an image subtitle (PGS/DVD) into the video, which means transcoding.</param>
/// <param name="Start">Where to start, in seconds; null to resume where the user stopped.</param>
public sealed record PlayRequest(int FileId, int? Audio, string? Subtitle, bool Burn, double? Start);

public sealed record ProgressRequest(int FileId, double Position, bool? Finished);

public static partial class PlaybackEndpoints
{
    [GeneratedRegex(@"^seg\d{5}\.ts$")]
    private static partial Regex SegmentName();

    [GeneratedRegex(@"^(?<kind>[sx])(?<n>\d{1,4})$")]
    private static partial Regex SubtitleId();

    // WebVTT made from a session's subtitles, kept while the session lasts
    private static readonly ConcurrentDictionary<string, byte[]> VttCache = new();

    public static void MapPlaybackEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/play", async (PlayRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db,
                                   LibraryFiles files, StreamSessionManager sessions, CancellationToken ct) =>
        {
            var userId = users.GetUserId(me)!;
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == req.FileId, ct);
            if (file is null) return Results.NotFound();
            if (file.ProbeError is not null || string.IsNullOrEmpty(file.ProbeJson))
                return Results.Json(new { error = "This file couldn't be read: " + file.ProbeError }, statusCode: 422);
            var path = files.FullPath(file);
            if (path is null || !File.Exists(path))
                return Results.Json(new { error = "The file isn't available right now (is the file server reachable?)." }, statusCode: 503);

            var probe = ProbeResult.Parse(file.ProbeJson);
            int? burn = null;
            if (req.Burn && req.Subtitle is { } sid && SubtitleId().Match(sid) is { Success: true } m && m.Groups["kind"].Value == "s")
                burn = int.Parse(m.Groups["n"].Value);
            var plan = PlaybackPlanner.Decide(probe, Path.GetExtension(path), req.Audio, burn);

            var start = req.Start;
            if (start is null)
            {
                var saved = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.MediaFileId == file.Id, ct);
                start = saved is { Finished: false } ? Math.Max(0, saved.PositionSeconds - 5) : 0;
            }
            start = Math.Clamp(start.Value, 0, Math.Max(0, file.DurationSeconds - 1));

            StreamSession session;
            try
            {
                session = await sessions.StartAsync(userId, file.Id, path, plan, start.Value);
                if (plan.Mode != PlaybackMode.Direct) await sessions.WaitForPlaylistAsync(session, ct);
            }
            catch (SessionLimitException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }

            var baseUrl = $"/stream/{session.Id}";
            var textSubs = probe.Subtitles.Where(s => s.IsTextSubtitle).Select(s => new { id = $"s{s.Index}", label = s.Label, lang = s.Language })
                .Concat(LibraryFiles.Sidecars(path).Select(x => new { id = $"x{x.Number}", label = x.Label, lang = (string?)null }))
                .Select(s => new { s.id, s.label, s.lang, url = $"{baseUrl}/subs/{s.id}.vtt" });
            return Results.Ok(new
            {
                sessionId = session.Id,
                mode = plan.Mode.ToString(),
                reason = plan.Reason,
                url = plan.Mode == PlaybackMode.Direct ? $"{baseUrl}/file" : $"{baseUrl}/index.m3u8",
                contentType = plan.Mode == PlaybackMode.Direct ? ContentType(path) : "application/vnd.apple.mpegurl",
                startSeconds = session.StartSeconds,
                // Direct play seeks itself; resume there instead
                seekTo = plan.Mode == PlaybackMode.Direct ? start : 0,
                duration = file.DurationSeconds,
                audio = plan.Audio?.Index,
                burnedSubtitle = plan.BurnSubtitle is { } b ? $"s{b.Index}" : null,
                subtitles = textSubs,
            });
        }).RequireRateLimiting("play");

        g.MapDelete("/play/{sessionId}", (string sessionId, ClaimsPrincipal me, UserManager<AppUser> users, StreamSessionManager sessions) =>
        {
            var s = sessions.Get(sessionId);
            if (s is not null && s.UserId == users.GetUserId(me)) sessions.Stop(sessionId);
            return Results.Ok();
        });

        g.MapPost("/progress", async (ProgressRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == req.FileId);
            if (file is null) return Results.NotFound();
            var p = await db.Progress.FirstOrDefaultAsync(x => x.UserId == userId && x.MediaFileId == req.FileId);
            if (p is null) db.Progress.Add(p = new PlaybackProgress { UserId = userId, MediaFileId = req.FileId });
            p.PositionSeconds = Math.Max(0, req.Position);
            // Into the end credits counts as watched
            p.Finished = req.Finished ?? (file.DurationSeconds > 0 && req.Position >= file.DurationSeconds * 0.93);
            p.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }

    /// <summary>
    /// The stream itself. No cookie: the session id in the path is the key (192 random bits, ends when the stream
    /// goes idle), which is what lets a Chromecast or another device fetch it. Only GET, CORS open, read-only.
    /// </summary>
    public static void MapStreamEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/stream/{sessionId}").RequireCors("stream");

        g.MapGet("/index.m3u8", async (string sessionId, StreamSessionManager sessions, HttpContext ctx, CancellationToken ct) =>
        {
            if (sessions.Get(sessionId) is not { } s || s.Plan.Mode == PlaybackMode.Direct) return Results.NotFound();
            try { await sessions.WaitForPlaylistAsync(s, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return Results.Problem(ex.Message, statusCode: 500); }
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(StreamSessionManager.ReadPlaylist(s) ?? "", "application/vnd.apple.mpegurl");
        });

        g.MapGet("/{segment}", async (string sessionId, string segment, StreamSessionManager sessions, HttpContext ctx, CancellationToken ct) =>
        {
            if (!SegmentName().IsMatch(segment)) return Results.NotFound();
            if (sessions.Get(sessionId) is not { } s || s.Plan.Mode == PlaybackMode.Direct) return Results.NotFound();
            if (!await sessions.WaitForSegmentAsync(s, segment, ct)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(Path.Combine(s.Directory, segment), "video/mp2t");
        });

        g.MapGet("/file", (string sessionId, StreamSessionManager sessions) =>
        {
            if (sessions.Get(sessionId) is not { Plan.Mode: PlaybackMode.Direct } s) return Results.NotFound();
            return Results.File(s.FullPath, ContentType(s.FullPath), enableRangeProcessing: true);
        });

        g.MapGet("/subs/{id}.vtt", async (string sessionId, string id, StreamSessionManager sessions, Ffmpeg ffmpeg, CancellationToken ct) =>
        {
            if (sessions.Get(sessionId) is not { } s) return Results.NotFound();
            var m = SubtitleId().Match(id);
            if (!m.Success) return Results.NotFound();
            var key = $"{sessionId}/{id}";
            if (!VttCache.TryGetValue(key, out var vtt))
            {
                string input = s.FullPath;
                int? stream = null;
                if (m.Groups["kind"].Value == "s") stream = int.Parse(m.Groups["n"].Value);
                else
                {
                    var side = LibraryFiles.Sidecars(s.FullPath).FirstOrDefault(x => x.Number == int.Parse(m.Groups["n"].Value));
                    if (side is null) return Results.NotFound();
                    input = side.Path;
                }
                try
                {
                    // Shifted by the stream's start, so cue times match the player's clock
                    vtt = await ffmpeg.RunAsync(ffmpeg.FfmpegPath, PlaybackPlanner.WebVttArguments(input, stream, s.StartSeconds), TimeSpan.FromMinutes(2), ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    return Results.Problem("Couldn't read these subtitles: " + ex.Message, statusCode: 500);
                }
                VttCache[key] = vtt;
                // Forget them once the session is gone
                foreach (var stale in VttCache.Keys.Where(k => !sessions.Exists(k.Split('/')[0])).ToList()) VttCache.TryRemove(stale, out _);
            }
            return Results.Bytes(vtt, "text/vtt; charset=utf-8");
        });
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "video/mp4",
    };
}
