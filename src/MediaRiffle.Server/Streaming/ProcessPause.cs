using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MediaRiffle.Server.Streaming;

/// <summary>Stops a process where it is and lets it go on (SIGSTOP and SIGCONT; NtSuspendProcess on Windows).</summary>
public static class ProcessPause
{
    private const int SigStop = 19, SigCont = 18;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")]
    private static extern int NtSuspendProcess(nint handle);

    [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")]
    private static extern int NtResumeProcess(nint handle);

    public static bool Suspend(Process p) => Send(p, suspend: true);
    public static bool Resume(Process p) => Send(p, suspend: false);

    /// <summary>False when it couldn't be done (the process ended meanwhile, or this system has no way): then it just keeps running.</summary>
    private static bool Send(Process p, bool suspend)
    {
        try
        {
            if (p.HasExited) return false;
            if (OperatingSystem.IsWindows()) return (suspend ? NtSuspendProcess(p.Handle) : NtResumeProcess(p.Handle)) == 0;
            return Kill(p.Id, suspend ? SigStop : SigCont) == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
                                       or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
