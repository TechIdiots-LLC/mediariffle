using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Streaming;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <param name="Subtitle">"s{stream}" for one in the file, "x{n}" for a side-car file, or null.</param>
/// <param name="Burn">Burn an image subtitle (PGS/DVD) into the video, which means transcoding.</param>
/// <param name="Start">Where to start, in seconds; null to resume where the user stopped.</param>
/// <param name="Quality">
/// "auto" (an adaptive stream of every level the user may have), "original", or a Quality.Ladder id ("1080", "720", …).
/// </param>
/// <param name="Hevc">The browser decodes HEVC Main (8-bit); <paramref name="Hevc10"/> Main 10.</param>
/// <param name="Adaptive">
/// For "original" picked by Auto: when the file can't be sent as it is (it would be transcoded), make it the adaptive
/// stream instead of one at the best level.
/// </param>
/// <param name="StartLevel">The level an adaptive stream's player should start at (listed first in its master playlist).</param>
public sealed record PlayRequest(int FileId, int? Audio, string? Subtitle, bool Burn, double? Start,
    string? Quality = null, bool Hevc = false, bool Hevc10 = false, bool Adaptive = false, string? StartLevel = null);

public sealed record ProgressRequest(int FileId, double Position, bool? Finished);

public static partial class PlaybackEndpoints
{
    [GeneratedRegex(@"^(?:seg\d{5}\.(?:ts|m4s)|init\.mp4)$")]
    private static partial Regex SegmentName();

    // Random, so no proxy or link can compress it: the speed test measures the real connection
    private static readonly byte[] SpeedTestBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1_000_000);

    [GeneratedRegex(@"^seg(\d{5,6})\.ts$")]
    private static partial Regex AdaptiveSegmentName();

    [GeneratedRegex(@"^(?<kind>[sx])(?<n>\d{1,4})$")]
    private static partial Regex SubtitleId();

    // WebVTT made from a session's subtitles, kept while the session lasts
    private static readonly ConcurrentDictionary<string, byte[]> VttCache = new();

    public static void MapPlaybackEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/play", async (PlayRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db,
                                   LibraryFiles files, StreamSessionManager sessions, Ffmpeg ffmpeg,
                                   IOptions<MediaRiffleOptions> options, CancellationToken ct) =>
        {
            var userId = users.GetUserId(me)!;
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == req.FileId, ct);
            if (file is null) return Results.NotFound();
            var path = files.FullPath(file);
            if (path is null || !File.Exists(path))
                return Results.Json(new { error = "The file isn't available right now (is the file server reachable?)." }, statusCode: 503);
            // Unreadable at the last scan (ffmpeg missing then, a share that was down, …): try again now
            if (file.ProbeError is not null || string.IsNullOrEmpty(file.ProbeJson))
            {
                var (json, error) = await ffmpeg.ProbeAsync(path, ct);
                if (json is null)
                    return Results.Json(new { error = "This file couldn't be read: " + error }, statusCode: 422);
                var fresh = ProbeResult.Parse(json);
                file.ProbeJson = json;
                file.ProbeError = null;
                file.DurationSeconds = fresh.DurationSeconds;
                file.Container = fresh.FormatName;
                file.VideoCodec = fresh.Video?.Codec ?? string.Empty;
                file.Width = fresh.Video?.Width ?? 0;
                file.Height = fresh.Video?.Height ?? 0;
                await db.SaveChangesAsync(ct);
            }

            var probe = ProbeResult.Parse(file.ProbeJson);
            int? burn = null;
            if (req.Burn && req.Subtitle is { } sid && SubtitleId().Match(sid) is { Success: true } m && m.Groups["kind"].Value == "s")
                burn = int.Parse(m.Groups["n"].Value);
            var account = await users.FindByIdAsync(userId);
            // Auto within the user's own preference (Profile → Video quality): Medium and Low adapt below a ceiling, as
            // well as the admin's cap, and never send the file as it is (Auto trying Original gets the adaptive stream)
            var autoCap = Media.Quality.Lower(account?.MaxQuality, Media.Quality.PreferenceCap(account?.PreferredQuality));
            var requested = req.Adaptive && autoCap != account?.MaxQuality ? Media.Quality.Auto : req.Quality;
            var quality = Media.Quality.Clamp(requested, account?.MaxQuality);
            var caps = new ClientCaps(req.Hevc, req.Hevc10);
            var t = options.Value.Transcoding;
            bool auto = quality == Media.Quality.Auto;
            var plan = PlaybackPlanner.Decide(probe, Path.GetExtension(path), req.Audio, burn,
                auto ? Media.Quality.Original : quality, caps, t);
            // What Original would be, so the player's Auto knows whether stepping up to it saves a transcode
            var originalMode = auto || quality == Media.Quality.Original ? plan.Mode
                : PlaybackPlanner.Decide(probe, Path.GetExtension(path), req.Audio, burn, Media.Quality.Original, caps, t).Mode;
            // Transcoded video is an adaptive stream: of every level for Auto, else of the one picked (which still
            // seeks without restarting). Sent as it is, or copied, it stays one stream.
            if (plan.Video is not null && (auto || plan.Mode == PlaybackMode.Transcode))
            {
                var all = Media.Quality.AdaptiveLevels(plan.Video.Height, t.MaxHeight, auto || req.Adaptive ? autoCap : account?.MaxQuality);
                IReadOnlyList<QualityLevel> levels = auto || req.Adaptive ? all
                    : Media.Quality.Level(quality) is { } one ? [one] : [all[0]];
                plan = plan with
                {
                    Mode = PlaybackMode.Transcode, Levels = levels, Segments = SegmentFormat.Ts,
                    Quality = levels.Count > 1 ? Media.Quality.Auto : levels[0].Id, StreamKbps = levels[0].TotalKbps,
                    Reason = levels.Count > 1 ? $"adaptive: {string.Join(", ", levels.Select(l => l.Label))}" : plan.Reason,
                };
            }

            // HDR video that's converted is tone-mapped to ordinary colours: with libplacebo when it works here (Dolby
            // Vision's metadata applied; not with VA-API, whose frames go to its own GPU device), else with zscale
            if (plan.Mode == PlaybackMode.Transcode && plan.Video is { IsHdr: true } hdr)
            {
                if (ffmpeg.CanLibplacebo && t.Encoder != VideoEncoder.Vaapi)
                    plan = plan with
                    {
                        Libplacebo = true,
                        Reason = plan.Reason + (hdr.DolbyVisionLabel is { } dv ? $"; HDR tone-mapped, {dv} applied" : "; HDR tone-mapped (libplacebo)"),
                    };
                else if (ffmpeg.CanToneMap)
                    plan = plan with
                    {
                        ToneMap = true,
                        Reason = plan.Reason + "; HDR tone-mapped" +
                                 (hdr.IsDolbyVisionOnly ? $" ({hdr.DolbyVisionLabel}'s colours need an ffmpeg with libplacebo and a Vulkan GPU)" : ""),
                    };
            }

            var start = req.Start;
            if (start is null)
            {
                var saved = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.MediaFileId == file.Id, ct);
                start = saved is { Finished: false } ? Math.Max(0, saved.PositionSeconds - 5) : 0;
            }
            start = Math.Clamp(start.Value, 0, Math.Max(0, file.DurationSeconds - 1));
            // Copied video starts at a keyframe; start the stream exactly there so its clock is right
            if (plan.Mode == PlaybackMode.Remux && start > 0 && plan.Video is { } v)
                start = await ffmpeg.KeyframeAtOrBeforeAsync(path, v.Index, start.Value);

            StreamSession session;
            try
            {
                session = await sessions.StartAsync(userId, file.Id, path, plan, start.Value, probe.ContentSeconds);
                session.FirstLevel = plan.Levels is { } ls ? Math.Max(0, ls.ToList().FindIndex(l => l.Id == req.StartLevel)) : 0;
                if (plan.Mode != PlaybackMode.Direct && !plan.IsAdaptive) await sessions.WaitForPlaylistAsync(session, ct);
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
                quality = plan.Quality,
                streamKbps = plan.StreamKbps,
                segments = plan.Segments.ToString(),
                // What the stream's video is: the file's own when it isn't transcoded (Cast needs to know)
                videoCodec = plan.Mode == PlaybackMode.Transcode ? "h264" : plan.Video?.Codec,
                url = plan.Mode == PlaybackMode.Direct ? $"{baseUrl}/file" : plan.IsAdaptive ? $"{baseUrl}/master.m3u8" : $"{baseUrl}/index.m3u8",
                contentType = plan.Mode == PlaybackMode.Direct ? ContentType(path) : "application/vnd.apple.mpegurl",
                startSeconds = session.StartSeconds,
                // Direct play and adaptive streams are the whole file and seek themselves; resume there instead
                seekTo = plan.Mode == PlaybackMode.Direct || plan.IsAdaptive ? start : 0,
                adaptive = plan.IsAdaptive,
                // An adaptive stream's levels, by their number in its playlists (v{n}/), best first
                levels = (plan.Levels ?? []).Select((l, i) => new
                {
                    id = l.Id, label = l.Label, kbps = l.VideoKbps + (plan.Audio is null ? 0 : l.AudioKbps),
                    height = PlaybackPlanner.OutputSize(plan.Video, l.Height).Height, path = $"v{i}/",
                }),
                originalMode = originalMode.ToString(),
                duration = plan.IsAdaptive ? session.DurationSeconds : file.DurationSeconds,
                audio = plan.Audio?.Index,
                burnedSubtitle = plan.BurnSubtitle is { } b ? $"s{b.Index}" : null,
                subtitles = textSubs,
            });
        }).RequireRateLimiting("play");

        // 1 MB the player times to choose Auto's starting quality
        g.MapGet("/speedtest", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(SpeedTestBytes, "application/octet-stream");
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
            p.Finished = req.Finished ?? NearlyDone(req.Position, file.DurationSeconds);
            // Watching it again puts it back on Continue watching, if its × took it off
            p.LeftContinue = false;
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

        // Adaptive streams: the master playlist, each level's full playlist, and segments made when asked for
        g.MapGet("/master.m3u8", (string sessionId, StreamSessionManager sessions, HttpContext ctx) =>
        {
            if (sessions.Get(sessionId) is not { Plan.IsAdaptive: true } s) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(PlaybackPlanner.MasterPlaylist(s.Plan, s.FirstLevel), "application/vnd.apple.mpegurl");
        });

        g.MapGet("/v{level:int}/index.m3u8", (string sessionId, int level, StreamSessionManager sessions, HttpContext ctx) =>
        {
            if (sessions.Get(sessionId) is not { Plan.IsAdaptive: true } s || level < 0 || level >= s.Plan.Levels!.Count) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(PlaybackPlanner.VariantPlaylist(s.DurationSeconds, s.SegmentSeconds), "application/vnd.apple.mpegurl");
        });

        g.MapGet("/v{level:int}/{segment}", async (string sessionId, int level, string segment, StreamSessionManager sessions, HttpContext ctx, CancellationToken ct) =>
        {
            var m = AdaptiveSegmentName().Match(segment);
            if (!m.Success || sessions.Get(sessionId) is not { Plan.IsAdaptive: true } s) return Results.NotFound();
            var made = await sessions.SegmentAsync(s, level, int.Parse(m.Groups[1].Value), ct);
            if (made.File is not { } file || !File.Exists(file)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            // For the player's level picking: time spent waiting for ffmpeg isn't the connection being slow
            ctx.Response.Headers["X-Encode-Wait"] = made.WaitMs.ToString(CultureInfo.InvariantCulture);
            if (made.Speed is { } speed) ctx.Response.Headers["X-Encode-Speed"] = speed.ToString("0.00", CultureInfo.InvariantCulture);
            return Results.File(file, "video/mp2t");
        });

        // The player's "still here" while paused, so a paused stream isn't stopped as idle
        g.MapGet("/alive", (string sessionId, StreamSessionManager sessions) =>
            sessions.Get(sessionId) is null ? Results.NotFound() : Results.NoContent());

        g.MapGet("/index.m3u8", async (string sessionId, StreamSessionManager sessions, HttpContext ctx, CancellationToken ct) =>
        {
            if (sessions.Get(sessionId) is not { } s || s.Plan.Mode == PlaybackMode.Direct || s.Plan.IsAdaptive) return Results.NotFound();
            try { await sessions.WaitForPlaylistAsync(s, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return Results.Problem(ex.Message, statusCode: 500); }
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(StreamSessionManager.ReadPlaylist(s) ?? "", "application/vnd.apple.mpegurl");
        });

        g.MapGet("/{segment}", async (string sessionId, string segment, StreamSessionManager sessions, HttpContext ctx, CancellationToken ct) =>
        {
            if (!SegmentName().IsMatch(segment)) return Results.NotFound();
            if (sessions.Get(sessionId) is not { } s || s.Plan.Mode == PlaybackMode.Direct) return Results.NotFound();
            if (!await sessions.WaitForSegmentAsync(s, segment, ct) || !File.Exists(Path.Combine(s.Directory, segment)))
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            var type = segment.EndsWith(".ts") ? "video/mp2t" : "video/mp4";
            return Results.File(Path.Combine(s.Directory, segment), type);
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
                    // The whole track, then shifted by the stream's start, so cue times match the player's clock
                    var full = await ffmpeg.RunAsync(ffmpeg.FfmpegPath, PlaybackPlanner.WebVttArguments(input, stream), TimeSpan.FromMinutes(2), ct);
                    vtt = WebVtt.Utf8(WebVtt.Shift(System.Text.Encoding.UTF8.GetString(full), s.StartSeconds));
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

    /// <summary>
    /// Stopped at the credits counts as watched: in the last 8%, or the last 3 minutes of anything over 10 minutes
    /// (a long movie's credits run longer than 8% leaves).
    /// </summary>
    public static bool NearlyDone(double position, double duration) =>
        duration > 0 && (position >= duration * 0.92 || (duration >= 600 && duration - position <= 180));

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "video/mp4",
    };
}
