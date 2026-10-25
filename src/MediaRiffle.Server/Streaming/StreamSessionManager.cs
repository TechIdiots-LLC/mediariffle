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

    // ── Adaptive streams (Plan.IsAdaptive): segments made when asked for, see PlaybackPlanner ──

    public double DurationSeconds { get; init; }
    public int SegmentSeconds { get; init; } = 4;
    public int SegmentCount => PlaybackPlanner.SegmentCount(DurationSeconds, SegmentSeconds);
    /// <summary>The level listed first in the master playlist, where a player without its own pick starts.</summary>
    public int FirstLevel { get; set; }
    /// <summary>The ffmpeg making segments now, or null (none asked for yet, or it was stopped for being far ahead).</summary>
    internal SegmentEncoder? Encoder { get; set; }
    internal SemaphoreSlim EncoderLock { get; } = new(1, 1);
    /// <summary>The finished segments, by level and number: their files (each run of ffmpeg writes to its own folder).</summary>
    internal ConcurrentDictionary<(int Level, int Segment), string> Segments { get; } = new();
    /// <summary>The latest segment the player asked for, to stop an ffmpeg that has run far ahead of it.</summary>
    internal int LastRequestedSegment { get; set; }
    /// <summary>
    /// The GPU failed on this stream's video (an ffmpeg decoding there gave up before its first segment): from then on
    /// it's decoded on the CPU, with the plan's HDR settings.
    /// </summary>
    internal bool CpuDecode { get; set; }
    /// <summary>The plan an ffmpeg starting now follows: without GPU decoding once that failed.</summary>
    internal PlaybackPlan EncoderPlan => CpuDecode ? Plan with { GpuDecode = false } : Plan;
}

/// <summary>One ffmpeg making one level of an adaptive stream, from <see cref="StartSegment"/> on.</summary>
internal sealed class SegmentEncoder(int level, int startSegment, string directory, Process process, bool gpuDecode = false)
{
    public int Level { get; } = level;
    public int StartSegment { get; } = startSegment;
    public string Directory { get; } = directory;
    public Process Process { get; } = process;
    /// <summary>Decoding on the GPU: when it fails (ffmpeg exits with an error), the stream goes on decoding on the CPU.</summary>
    public bool GpuDecode { get; } = gpuDecode;
    /// <summary>Decoding on the GPU and it failed. What it wrote as it ended isn't used (it may have no picture).</summary>
    public bool GpuFailed => GpuDecode && Process.HasExited && Process.ExitCode != 0;
    /// <summary>The last segment this run finished (StartSegment - 1 before the first).</summary>
    public int Latest { get; private set; } = startSegment - 1;
    private long _read;

    private readonly StringBuilder _errors = new();
    public void AddError(string line) { lock (_errors) { if (_errors.Length < 8000) _errors.AppendLine(line); } }
    public string Errors { get { lock (_errors) return _errors.ToString(); } }

    /// <summary>Where it had got to (seconds of video) and when (Environment.TickCount64), over the last 10 s or so.</summary>
    private readonly Queue<(long Ms, double Out)> _progress = new();

    /// <summary>Video frames written so far (frame= in -progress).</summary>
    public long Frames => Interlocked.Read(ref _frames);
    private long _frames;

    /// <summary>A line of ffmpeg's -progress output: out_time_us is how far it has written, frame= how many pictures.</summary>
    public void Progress(string line)
    {
        if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6), out var frames))
            Interlocked.Exchange(ref _frames, frames);
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), out var us) || us <= 0) return;
        long now = Environment.TickCount64;
        lock (_progress)
        {
            _progress.Enqueue((now, us / 1e6));
            while (_progress.Count > 2 && now - _progress.Peek().Ms > 10_000) _progress.Dequeue();
        }
    }

    /// <summary>
    /// Seconds of video made per second lately: under 1, it falls behind playing. Null until it has written for a few
    /// seconds, so starting up (seeking, the first frames) doesn't count. Measured to now, so a stall pulls it down.
    /// </summary>
    public double? Speed
    {
        get
        {
            lock (_progress)
            {
                if (_progress.Count < 2) return null;
                var first = _progress.Peek();
                double span = (Environment.TickCount64 - first.Ms) / 1000.0;
                return span < 3 ? null : (_progress.Last().Out - first.Out) / span;
            }
        }
    }

    /// <summary>Adds the segments ffmpeg has finished since the last look (its list.csv gets a line per finished one).</summary>
    public void Collect(StreamSession s)
    {
        lock (this)
        {
            try
            {
                var list = Path.Combine(Directory, "list.csv");
                // Decoding on the GPU, a segment counts only once pictures come out: one written as it fails (a size or
                // profile the GPU can't decode) has sound but no picture, and is often listed before ffmpeg has exited
                if (GpuFailed || (GpuDecode && Frames == 0) || !File.Exists(list)) return;
                using var f = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (f.Length <= _read) return;
                f.Seek(_read, SeekOrigin.Begin);
                var buf = new byte[f.Length - _read];
                int n = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                var text = Encoding.UTF8.GetString(buf, 0, n);
                int end = text.LastIndexOf('\n');
                if (end < 0) return;   // only whole lines
                _read += Encoding.UTF8.GetByteCount(text.AsSpan(0, end + 1));
                foreach (var line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // seg00012.ts,48.089711,52.093711
                    var name = line.Split(',')[0].Trim();
                    if (!name.StartsWith("seg") || !name.EndsWith(".ts") || !int.TryParse(name.AsSpan(3, name.Length - 6), out var k)) continue;
                    s.Segments[(Level, k)] = Path.Combine(Directory, name);
                    if (k > Latest) Latest = k;
                }
            }
            catch (IOException) { }
        }
    }
}

/// <summary>
/// A segment of an adaptive stream: its file (null when it couldn't be made), how long the request waited for ffmpeg
/// to make it, and how fast that level's ffmpeg is going (seconds of video a second), when known. The player keeps
/// the wait out of its measure of the connection, and doesn't pick a level ffmpeg can't keep up with.
/// </summary>
public sealed record SegmentResult(string? File, int WaitMs = 0, double? Speed = null);

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

    /// <summary>How many streams are running (anything else reading the libraries waits while there are any).</summary>
    public int Count => _sessions.Count;

    /// <summary>Whether a session is still running, without counting as activity on it.</summary>
    public bool Exists(string id) => _sessions.ContainsKey(id);

    /// <param name="startSeconds">Where ffmpeg is asked to start (-ss).</param>
    /// <param name="streamStart">Where the stream really starts, when that differs (copied video starts at a keyframe before it).</param>
    /// <param name="replacing">
    /// The user's stream of this file that this one takes over from: it keeps running (the player closes it once it has
    /// swapped to the new one), and doesn't count against the limits meanwhile. Ignored unless it's that.
    /// </param>
    public async Task<StreamSession> StartAsync(string userId, int fileId, string fullPath, PlaybackPlan plan, double startSeconds, double durationSeconds = 0,
        double? streamStart = null, string? replacing = null)
    {
        await _startLock.WaitAsync();
        try
        {
            var keep = replacing is not null && _sessions.TryGetValue(replacing, out var r) && r.UserId == userId && r.FileId == fileId
                ? replacing : null;
            // A user watching the same file again (a seek, a new tab) replaces their old stream of it
            foreach (var old in _sessions.Values.Where(s => s.UserId == userId && s.FileId == fileId && s.Id != keep).ToList())
                Stop(old.Id);
            var mine = _sessions.Values.Where(s => s.UserId == userId && s.Id != keep).OrderBy(s => s.LastAccessUtc).ToList();
            while (mine.Count > 0 && mine.Count >= T.MaxSessionsPerUser)
            {
                Stop(mine[0].Id);
                mine.RemoveAt(0);
            }
            if (plan.Mode != PlaybackMode.Direct &&
                _sessions.Values.Count(s => s.Plan.Mode != PlaybackMode.Direct && s.Id != keep) >= T.MaxSessions)
                throw new SessionLimitException($"The server is already running {T.MaxSessions} streams; try again when one ends.");

            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
            var dir = Path.Combine(options.Value.TranscodeDirectory, id);
            var session = new StreamSession
            {
                Id = id, UserId = userId, FileId = fileId, FullPath = fullPath, Plan = plan,
                // An adaptive stream is the whole file on its own clock; the player starts it at startSeconds
                StartSeconds = plan.Mode == PlaybackMode.Direct || plan.IsAdaptive ? 0 : streamStart ?? startSeconds, Directory = dir,
                DurationSeconds = durationSeconds, SegmentSeconds = Math.Max(1, T.SegmentSeconds),
            };
            if (plan.IsAdaptive)
            {
                // Nothing runs until the player asks for a segment (SegmentAsync)
                System.IO.Directory.CreateDirectory(dir);
                log.LogInformation("Session {Id}: adaptive {Levels} of file {File} ({Reason})", id,
                    string.Join("/", plan.Levels!.Select(l => l.Label)), fileId, plan.Reason);
            }
            else if (plan.Mode != PlaybackMode.Direct)
            {
                System.IO.Directory.CreateDirectory(dir);
                var args = PlaybackPlanner.HlsArguments(plan, fullPath, startSeconds, T);
                var psi = ffmpeg.StartInfo(ffmpeg.FfmpegPath, args);
                psi.WorkingDirectory = dir;   // the playlist and segments are written here, by relative name
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
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
        StopEncoder(s);
        DeleteFolder(s.Directory);
    }

    // ── Adaptive streams ──

    /// <summary>How far the next segment asked for may be past what ffmpeg has made, to wait for it instead of restarting there.</summary>
    private const int WaitAhead = 2;
    /// <summary>An ffmpeg this far (in seconds) ahead of what the player asks for is stopped, e.g. while paused; it starts again when needed.</summary>
    private const int MaxAheadSeconds = 180;

    /// <summary>
    /// Segment <paramref name="segment"/> of level <paramref name="level"/>: the file, once made. Made already, it's
    /// there; coming soon from the ffmpeg running for that level, it's waited for; otherwise that ffmpeg is replaced
    /// by one making this level from this segment on (one ffmpeg per stream). File is null when it couldn't be made.
    /// </summary>
    public async Task<SegmentResult> SegmentAsync(StreamSession s, int level, int segment, CancellationToken ct)
    {
        if (!s.Plan.IsAdaptive || level < 0 || level >= s.Plan.Levels!.Count || segment < 0 || segment >= s.SegmentCount) return new(null);
        Stopwatch? waiting = null;   // started when it has to be made
        s.LastRequestedSegment = segment;
        if (s.Segments.TryGetValue((level, segment), out var done)) return Made(done);

        waiting = Stopwatch.StartNew();
        SegmentEncoder? mine;
        await s.EncoderLock.WaitAsync(ct);
        try
        {
            if (!_sessions.ContainsKey(s.Id)) return new(null);   // stopped meanwhile
            var e = s.Encoder;
            e?.Collect(s);
            if (s.Segments.TryGetValue((level, segment), out done)) return Made(done);
            bool coming = e is { Process.HasExited: false } && e.Level == level &&
                          segment >= e.StartSegment && segment <= Math.Max(e.Latest, e.StartSegment - 1) + WaitAhead;
            mine = coming ? e! : StartEncoder(s, level, segment);
        }
        finally { s.EncoderLock.Release(); }

        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            // Decoding on the GPU failed (a size or profile it can't decode after all, a driver problem): the same
            // again, decoded on the CPU, for this stream from now on. Checked first: ffmpeg writes out a segment as
            // it gives up, which may have sound but no picture.
            if (mine.GpuFailed && !s.CpuDecode && s.Encoder == mine)
            {
                await s.EncoderLock.WaitAsync(ct);
                try
                {
                    if (s.Encoder == mine && _sessions.ContainsKey(s.Id))
                    {
                        // Its last segment, if it was read just before ffmpeg ended, is made again
                        if (s.Segments.TryGetValue((mine.Level, mine.Latest), out var last) && last.StartsWith(mine.Directory, StringComparison.Ordinal))
                            s.Segments.TryRemove((mine.Level, mine.Latest), out _);
                        s.CpuDecode = true;
                        log.LogWarning("Session {Id}: the GPU couldn't decode this video ({Errors}); decoding it on the CPU",
                            s.Id, Ffmpeg.LastLines(mine.Errors, 2));
                        mine = StartEncoder(s, level, segment);
                        continue;
                    }
                }
                finally { s.EncoderLock.Release(); }
            }
            mine.Collect(s);
            if (s.Segments.TryGetValue((level, segment), out done)) return Made(done);
            // Replaced (the player switched level or seeked meanwhile), or ffmpeg ended without it
            if (s.Encoder != mine || mine.Process.HasExited)
            {
                mine.Collect(s);
                return Made(s.Segments.GetValueOrDefault((level, segment)));
            }
            s.LastAccessUtc = DateTime.UtcNow;   // a slow encode isn't an idle viewer
            await Task.Delay(100, ct);
        }
        return new(null);

        // With how long it was waited for, and how fast this level's ffmpeg is going (when it's the one running)
        SegmentResult Made(string? file) => new(file, (int)(waiting?.ElapsedMilliseconds ?? 0),
            s.Encoder is { Level: var l, Process.HasExited: false } now && l == level ? now.Speed : null);
    }

    /// <summary>Replaces the session's ffmpeg with one making <paramref name="level"/> from <paramref name="segment"/> on. Under EncoderLock.</summary>
    private SegmentEncoder StartEncoder(StreamSession s, int level, int segment)
    {
        StopEncoder(s);
        var lvl = s.Plan.Levels![level];
        // Each run writes to its own folder, so a segment being sent is never overwritten by another run
        var dir = Path.Combine(s.Directory, $"v{level}", $"r{segment}-{Environment.TickCount64}");
        System.IO.Directory.CreateDirectory(dir);
        var psi = ffmpeg.StartInfo(ffmpeg.FfmpegPath, PlaybackPlanner.SegmentArguments(s.EncoderPlan, lvl, s.FullPath, segment, T));
        psi.WorkingDirectory = dir;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var e = new SegmentEncoder(level, segment, dir, p, s.EncoderPlan.GpuDecode);
        p.ErrorDataReceived += (_, a) => { if (a.Data is { Length: > 0 } line) e.AddError(line); };
        p.OutputDataReceived += (_, a) => { if (a.Data is { Length: > 0 } line) e.Progress(line); };
        p.Exited += (_, _) =>
        {
            // Killed on purpose (replaced, stopped) isn't news; ffmpeg failing on its own is
            if (p.ExitCode != 0 && s.Encoder == e && _sessions.ContainsKey(s.Id))
                log.LogWarning("ffmpeg for session {Id} ({Level} from segment {Segment}) exited {Code}: {Errors}",
                    s.Id, lvl.Label, segment, p.ExitCode, Ffmpeg.LastLines(e.Errors, 3));
        };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        s.Encoder = e;
        log.LogInformation("Session {Id}: {Level} from {Start}s", s.Id, lvl.Label, segment * s.SegmentSeconds);
        return e;
    }

    private static void StopEncoder(StreamSession s)
    {
        if (s.Encoder is not { } e) return;
        s.Encoder = null;
        try { Ffmpeg.Kill(e.Process); e.Process.Dispose(); } catch (InvalidOperationException) { }
    }

    /// <summary>Stops an ffmpeg that has run far ahead of the player (paused, or a long buffer); it's started again from where it's needed.</summary>
    private void ThrottleEncoders()
    {
        foreach (var s in _sessions.Values.Where(x => x.Plan.IsAdaptive))
        {
            if (s.Encoder is not { } e || e.Process.HasExited) continue;
            e.Collect(s);
            if ((e.Latest - s.LastRequestedSegment) * s.SegmentSeconds <= MaxAheadSeconds) continue;
            if (!s.EncoderLock.Wait(0)) continue;
            try
            {
                if (s.Encoder != e) continue;
                StopEncoder(s);
                log.LogDebug("Session {Id}: paused making segments, {Ahead}s ahead", s.Id, (e.Latest - s.LastRequestedSegment) * s.SegmentSeconds);
            }
            finally { s.EncoderLock.Release(); }
        }
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
                ThrottleEncoders();
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
