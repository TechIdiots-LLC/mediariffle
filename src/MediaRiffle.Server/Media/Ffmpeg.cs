using System.Diagnostics;
using System.Text;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Media;

/// <summary>
/// Finds and starts ffmpeg and ffprobe. Arguments always go in as a list (ProcessStartInfo.ArgumentList), never
/// through a shell, so no file name can be read as a command, whatever characters it has.
/// </summary>
public sealed class Ffmpeg(IOptions<MediaRiffleOptions> options, ILogger<Ffmpeg> log)
{
    public string FfmpegPath => Tool("ffmpeg");
    public string FfprobePath => Tool("ffprobe");

    private string Tool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var folder = options.Value.Transcoding.FfmpegFolder;
        // Absolute, as ffmpeg runs in each session's folder
        return string.IsNullOrWhiteSpace(folder) ? exe : Path.GetFullPath(Path.Combine(folder, exe));
    }

    public ProcessStartInfo StartInfo(string tool, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // so 'q' can ask ffmpeg to finish cleanly
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs a tool to completion and returns its standard output, or throws with its error output.</summary>
    public async Task<byte[]> RunAsync(string tool, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(tool, args) };
        p.Start();
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = new MemoryStream();
        var copyOut = p.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token);
        var readErr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await Task.WhenAll(copyOut, readErr, p.WaitForExitAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            throw new TimeoutException($"{Path.GetFileName(tool)} took longer than {timeout.TotalSeconds:0} s");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool)} failed: {LastLines(await readErr, 4)}");
        return stdout.ToArray();
    }

    /// <summary>
    /// ffprobe's JSON for a file, or the reason it couldn't be read, in words an admin can act on (ffprobe missing
    /// is said as such, not as a process-start error).
    /// </summary>
    public async Task<(string? Json, string? Error)> ProbeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var bytes = await RunAsync(FfprobePath,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], TimeSpan.FromSeconds(90), ct);
            return (Encoding.UTF8.GetString(bytes), null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, $"ffprobe isn't installed where MediaRiffle looks for it ({FfprobePath}). Install ffmpeg, or set " +
                          "MediaRiffle:Transcoding:FfmpegFolder, then restart MediaRiffle.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Where a remux started with <c>-noaccurate_seek -ss <paramref name="seconds"/></c> really begins, in seconds from
    /// the start of the file (<paramref name="fileStart"/>: its format start_time). Copied video can only start at a
    /// keyframe, and which one ffmpeg lands on isn't simply the last at or before the time: it seeks a little early in
    /// files with B-frames, and Matroska only to its cue points. So the same seek is made here, copying one video
    /// packet, and that packet's time read. Null when it can't tell.
    /// </summary>
    public async Task<double?> RemuxStartAsync(string path, int videoStream, double seconds, double fileStart)
    {
        try
        {
            var output = Encoding.UTF8.GetString(await RunAsync(FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-noaccurate_seek", "-copyts",
                 "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", path,
                 "-map", $"0:{videoStream.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-c", "copy",
                 "-frames:v", "1", "-f", "framecrc", "-"],
                TimeSpan.FromSeconds(20)));
            return FirstPacketSeconds(output) is { } t ? t - fileStart : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            log.LogDebug("No remux start for {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>The first packet's pts in seconds, from ffmpeg's framecrc output ("#tb 0: 1/1000", then "0, dts, pts, …").</summary>
    public static double? FirstPacketSeconds(string framecrc)
    {
        double? tb = null;
        foreach (var raw in framecrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var parts = line[6..].Trim().Split('/');
                if (parts.Length == 2 && long.TryParse(parts[0], out var num) && long.TryParse(parts[1], out var den) && den > 0)
                    tb = (double)num / den;
            }
            else if (!line.StartsWith('#') && line.Length > 0 && tb is { } t)
            {
                var cols = line.Split(',');
                return cols.Length > 2 && long.TryParse(cols[2].Trim(), out var pts) ? pts * t : null;
            }
        }
        return null;
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(count));
    }

    /// <summary>Checks ffmpeg and ffprobe start, logging their versions; returns false (and logs why) if not.</summary>
    public async Task<bool> CheckAsync()
    {
        foreach (var tool in new[] { FfmpegPath, FfprobePath })
        {
            try
            {
                var output = Encoding.UTF8.GetString(await RunAsync(tool, ["-version"], TimeSpan.FromSeconds(10)));
                log.LogInformation("{Tool}: {Version}", Path.GetFileName(tool), output.Split('\n')[0].Trim());
            }
            catch (Exception ex)
            {
                log.LogError("Can't run {Tool}: {Error}. Set MediaRiffle:Transcoding:FfmpegFolder, or install ffmpeg.", tool, ex.Message);
                return false;
            }
        }
        await CheckToneMappingAsync();
        await CheckLibplaceboAsync();
        return true;
    }

    /// <summary>
    /// This ffmpeg can tone-map HDR to SDR (it has zscale, from libzimg, and tonemap) and it's turned on
    /// (Transcoding:ToneMapHdr). Found out at start, by <see cref="CheckAsync"/>.
    /// </summary>
    public bool CanToneMap { get; private set; }

    private async Task CheckToneMappingAsync()
    {
        if (!options.Value.Transcoding.ToneMapHdr) return;
        try
        {
            var filters = Encoding.UTF8.GetString(await RunAsync(FfmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(10)));
            CanToneMap = HasFilter(filters, "zscale") && HasFilter(filters, "tonemap");
            if (!CanToneMap)
                log.LogWarning("This ffmpeg has no zscale filter (libzimg), so HDR video converted for a browser will look washed out. " +
                               "Builds from ffmpeg.org's links (BtbN, gyan.dev) and most Linux packages have it.");
        }
        catch (Exception ex) { log.LogWarning("Couldn't list ffmpeg's filters: {Error}", ex.Message); }
    }

    /// <summary>
    /// This ffmpeg has libplacebo and a Vulkan GPU it can run on (found out at start, by trying it on a tiny HDR
    /// picture), and Transcoding:UseLibplacebo is on. Then HDR is tone-mapped with it, which applies Dolby Vision's
    /// metadata (the only way to convert Dolby Vision 5 right) and handles HDR10 better than zscale.
    /// </summary>
    public bool CanLibplacebo { get; private set; }

    private async Task CheckLibplaceboAsync()
    {
        if (!options.Value.Transcoding.ToneMapHdr || !options.Value.Transcoding.UseLibplacebo) return;
        try
        {
            await RunAsync(FfmpegPath, LibplaceboTestArguments, TimeSpan.FromSeconds(30));
            CanLibplacebo = true;
            log.LogInformation("HDR and Dolby Vision are tone-mapped with libplacebo (Vulkan)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            log.LogInformation("libplacebo isn't usable here ({Error}); HDR is tone-mapped with zscale, and Dolby Vision 5 " +
                               "won't convert to the right colours. It needs an ffmpeg with libplacebo and a GPU with Vulkan.", ex.Message);
        }
    }

    /// <summary>One frame of a small 10-bit HDR picture through libplacebo on Vulkan, to nowhere.</summary>
    public static readonly string[] LibplaceboTestArguments =
    [
        "-hide_banner", "-v", "error", "-init_hw_device", "vulkan",
        "-f", "lavfi", "-i", "color=c=gray:s=64x64:d=0.04,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
        "-vf", "libplacebo=colorspace=bt709:color_primaries=bt709:color_trc=bt709:format=yuv420p", "-frames:v", "1", "-f", "null", "-",
    ];

    /// <summary>Whether ffmpeg's <c>-filters</c> list has this filter (a line like " ..C zscale  V->V  Apply resizing…").</summary>
    public static bool HasFilter(string filterList, string name) =>
        filterList.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } p && p[1] == name);
}
