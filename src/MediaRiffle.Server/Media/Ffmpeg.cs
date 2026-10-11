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
        return string.IsNullOrWhiteSpace(folder) ? exe : Path.Combine(folder, exe);
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
        return true;
    }
}
