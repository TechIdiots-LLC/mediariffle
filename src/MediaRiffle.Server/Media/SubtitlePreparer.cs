using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Media;

/// <summary>
/// Extracts the libraries' subtitle tracks ahead of time (SubtitleCache), so turning one on is instant: a file at a
/// time, newest first, only while nothing is streaming (it reads the whole file, and playback comes first), and only
/// until the cache is full. Looks again every half hour, which picks up what a scan added. MediaRiffle:PrepareSubtitles.
/// </summary>
public sealed class SubtitlePreparer(IServiceScopeFactory scopes, SubtitleCache cache, StreamSessionManager sessions,
    IOptions<MediaRiffleOptions> options, ILogger<SubtitlePreparer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PrepareSubtitles) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);   // after the first scan has had a start
            while (!stoppingToken.IsCancellationRequested)
            {
                // A failure here (the database busy, a file that can't be read) is for the next round, not the end of the
                // server: an uncaught one in a background service stops the whole host
                try { await PrepareAllAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Preparing subtitles failed; trying again in 30 minutes");
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PrepareAllAsync(CancellationToken ct)
    {
        List<(string Path, IReadOnlyList<SubtitleTrack> Tracks)> todo;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<LibraryFiles>();
            var rows = await db.MediaFiles.AsNoTracking()
                .Where(f => (f.Kind == MediaKind.Movie || f.Kind == MediaKind.Episode) && f.ProbeJson != "")
                .OrderByDescending(f => f.ModifiedUtc).ToListAsync(ct);
            todo = rows.Select(f => (Path: files.FullPath(f), Tracks: Tracks(f.ProbeJson)))
                .Where(x => x.Path is not null && x.Tracks.Count > 0 && !cache.Has(x.Path, x.Tracks))
                .Select(x => (x.Path!, x.Tracks)).ToList();
        }
        if (todo.Count == 0) return;
        log.LogInformation("Preparing subtitles: {Count} file(s) to go", todo.Count);
        int done = 0;
        foreach (var (path, tracks) in todo)
        {
            if (cache.Size() >= cache.CapBytes * 0.95)
            {
                log.LogInformation("Preparing subtitles: the cache is full ({GB} GB, SubtitleCacheGB); {Left} file(s) left for when they're played",
                    options.Value.SubtitleCacheGB, todo.Count - done);
                return;
            }
            // Playback first: wait while anything is streaming
            while (sessions.Count > 0) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            if (!File.Exists(path)) continue;
            await cache.ExtractAsync(path, tracks);
            done++;
        }
        log.LogInformation("Preparing subtitles: done ({Count} file(s))", done);
    }

    private static IReadOnlyList<SubtitleTrack> Tracks(string probeJson)
    {
        try { return SubtitleTrack.Of(ProbeResult.Parse(probeJson)); }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
