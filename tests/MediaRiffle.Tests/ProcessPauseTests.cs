using System.Diagnostics;
using MediaRiffle.Server.Streaming;

namespace MediaRiffle.Tests;

public class ProcessPauseTests
{
    private static Process Sleeper() => Process.Start(new ProcessStartInfo(
        OperatingSystem.IsWindows() ? "ping" : "sleep", OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30")
    { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false })!;

    [Fact]
    public void ARunningProcessCanBeStoppedAndLetGo()
    {
        using var p = Sleeper();
        try
        {
            Assert.True(ProcessPause.Suspend(p));
            Assert.False(p.HasExited);
            Assert.True(ProcessPause.Resume(p));
            Assert.False(p.HasExited);
        }
        finally { p.Kill(); }
    }

    [Fact]
    public void AStoppedProcessCanStillBeKilled()
    {
        using var p = Sleeper();
        Assert.True(ProcessPause.Suspend(p));
        p.Kill(entireProcessTree: true);
        Assert.True(p.WaitForExit(5000));
    }

    [Fact]
    public void AProcessThatEndedCantBeStopped()
    {
        using var p = Sleeper();
        p.Kill();
        p.WaitForExit();
        Assert.False(ProcessPause.Suspend(p));
        Assert.False(ProcessPause.Resume(p));
    }
}

public class PausedFfmpegTests
{
    /// <summary>A real ffmpeg making HLS segments as fast as it can writes nothing while stopped, and goes on when let go.</summary>
    [Fact]
    public async Task AStoppedFfmpegWritesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mr-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { WorkingDirectory = dir, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=100000",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                "-hls_flags", "temp_file", "-hls_segment_filename", "seg%05d.ts", "index.m3u8" }) psi.ArgumentList.Add(a);
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { return; }   // no ffmpeg here
            p!.BeginErrorReadLine(); p.BeginOutputReadLine();
            int Segments() => Directory.GetFiles(dir, "seg*.ts").Length;

            await Task.Delay(3000);
            Assert.True(ProcessPause.Suspend(p));
            await Task.Delay(500);   // what was in flight
            int stopped = Segments();
            Assert.True(stopped > 0);
            await Task.Delay(3000);
            Assert.Equal(stopped, Segments());

            Assert.True(ProcessPause.Resume(p));
            await Task.Delay(3000);
            Assert.True(Segments() > stopped);
        }
        finally
        {
            try { p?.Kill(entireProcessTree: true); p?.WaitForExit(5000); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
