using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Streaming;

/// <summary>One viewer playing one file: a direct file, or an ffmpeg process writing HLS segments.</summary>
public sealed class StreamSession
{
    /// <summary>Unguessable (192 random bits); the stream URLs carry it, which is how a Chromecast is let in.</summary>
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required int FileId { get; init; }
    public required string FullPath { get; init; }
    public required PlaybackPlan Plan { get; init; }
    /// <summary>Where in the file the stream starts; the player adds it to its own clock.</summary>
    public required double StartSeconds { get; init; }
    public required string Directory { get; init; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    public Process? Process { get; set; }

    private readonly StringBuilder _errors = new();
    public void AddError(string line) { lock (_errors) { if (_errors.Length < 8000) _errors.AppendLine(line); } }
    public string Errors { get { lock (_errors) return _errors.ToString(); } }

    public bool HasExited => Process is { HasExited: true };
    public bool FailedEarly => Process is { HasExited: true, ExitCode: not 0 };

    public string PlaylistPath => Path.Combine(Directory, "index.m3u8");
}

public sealed class SessionLimitException(string message) : Exception(message);

/// <summary>
/// Starts, finds and stops stream sessions. Each HLS session is one ffmpeg process writing into its own folder
/// under the transcode directory; a session nobody fetches from for IdleTimeoutSeconds is stopped and deleted,
/// as is everything there when the server starts. Replaces Tongue's fixed pool of ffserver feeds.
/// </summary>
public sealed class StreamSessionManager(IOptions<MediaRiffleOptions> options, Ffmpeg ffmpeg, ILogger<StreamSessionManager> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private TranscodingOptions T => options.Value.Transcoding;

    public StreamSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        s.LastAccessUtc = DateTime.UtcNow;
        return s;
    }

    /// <summary>Whether a session is still running, without counting as activity on it.</summary>
    public bool Exists(string id) => _sessions.ContainsKey(id);

    public async Task<StreamSession> StartAsync(string userId, int fileId, string fullPath, PlaybackPlan plan, double startSeconds)
    {
        await _startLock.WaitAsync();
        try
        {
            // A user watching the same file again (a seek, a new tab) replaces their old stream of it
            foreach (var old in _sessions.Values.Where(s => s.UserId == userId && s.FileId == fileId).ToList())
                Stop(old.Id);
            var mine = _sessions.Values.Where(s => s.UserId == userId).OrderBy(s => s.LastAccessUtc).ToList();
            while (mine.Count >= T.MaxSessionsPerUser)
            {
                Stop(mine[0].Id);
                mine.RemoveAt(0);
            }
            if (plan.Mode != PlaybackMode.Direct &&
                _sessions.Values.Count(s => s.Plan.Mode != PlaybackMode.Direct) >= T.MaxSessions)
                throw new SessionLimitException($"The server is already running {T.MaxSessions} streams; try again when one ends.");

            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
            var dir = Path.Combine(options.Value.TranscodeDirectory, id);
            var session = new StreamSession
            {
                Id = id, UserId = userId, FileId = fileId, FullPath = fullPath, Plan = plan,
                StartSeconds = plan.Mode == PlaybackMode.Direct ? 0 : startSeconds, Directory = dir,
            };
            if (plan.Mode != PlaybackMode.Direct)
            {
                System.IO.Directory.CreateDirectory(dir);
                var args = PlaybackPlanner.HlsArguments(plan, fullPath, startSeconds, dir, T);
                var p = new Process { StartInfo = ffmpeg.StartInfo(ffmpeg.FfmpegPath, args), EnableRaisingEvents = true };
                p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) session.AddError(line); };
                p.Exited += (_, _) =>
                {
                    if (p.ExitCode != 0 && _sessions.ContainsKey(id))
                        log.LogWarning("ffmpeg for session {Id} ({Mode}) exited {Code}: {Errors}", id, plan.Mode, p.ExitCode,
                            Ffmpeg.LastLines(session.Errors, 3));
                };
                p.Start();
                p.BeginErrorReadLine();
                p.StandardOutput.Close();
                session.Process = p;
                log.LogInformation("Session {Id}: {Mode} of file {File} from {Start:0}s ({Reason})", id, plan.Mode, fileId, startSeconds, plan.Reason);
            }
            _sessions[id] = session;
            return session;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void Stop(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return;
        if (s.Process is { } p)
        {
            Ffmpeg.Kill(p);
            p.Dispose();
        }
        DeleteFolder(s.Directory);
    }

    public IReadOnlyCollection<StreamSession> All => _sessions.Values.ToList();

    /// <summary>Waits until the playlist lists its first segment; throws if ffmpeg gave up first.</summary>
    public async Task WaitForPlaylistAsync(StreamSession s, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadPlaylist(s) is { } text && text.Contains("#EXTINF")) return;
            if (s.HasExited) throw new InvalidOperationException("The stream couldn't start: " + Ffmpeg.LastLines(s.Errors, 2));
            await Task.Delay(200, ct);
        }
        throw new TimeoutException("The stream took too long to start.");
    }

    /// <summary>
    /// Waits until <paramref name="segment"/> is listed in the playlist, which with temp_file means it's complete.
    /// Returns false if the stream ended without it (asked for past the end).
    /// </summary>
    public async Task<bool> WaitForSegmentAsync(StreamSession s, string segment, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var text = ReadPlaylist(s);
            if (text is not null && text.Contains(segment)) return true;
            if (s.HasExited) return false;
            s.LastAccessUtc = DateTime.UtcNow;   // a slow encode isn't an idle viewer
            await Task.Delay(200, ct);
        }
        return false;
    }

    public static string? ReadPlaylist(StreamSession s)
    {
        try { return File.Exists(s.PlaylistPath) ? File.ReadAllText(s.PlaylistPath) : null; }
        catch (IOException) { return null; }   // being replaced by ffmpeg this instant
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Left over from a previous run: nothing can be playing them now
        DeleteFolder(options.Value.TranscodeDirectory);
        Directory.CreateDirectory(options.Value.TranscodeDirectory);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var idle = TimeSpan.FromSeconds(T.IdleTimeoutSeconds);
                foreach (var s in _sessions.Values.Where(s => DateTime.UtcNow - s.LastAccessUtc > idle).ToList())
                {
                    log.LogInformation("Session {Id} idle, stopping", s.Id);
                    Stop(s.Id);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var id in _sessions.Keys.ToList()) Stop(id);
        }
    }

    private void DeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { log.LogDebug("Couldn't delete {Dir}: {Error}", dir, ex.Message); }
    }
}
