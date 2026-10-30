using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaRiffle.Server.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Streaming;

/// <summary>
/// Answers /healthz (ahead of everything else, so a redirect or sign-in can't hide a problem): the web server answers,
/// and so does the database. HAProxy's health check and <see cref="Watchdog"/> both use it.
/// </summary>
public static class Health
{
    public const string Path = "/healthz";

    public static IApplicationBuilder UseHealth(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.Equals(Path, StringComparison.Ordinal)) { await next(); return; }
        ctx.Response.Headers.CacheControl = "no-store";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            await ctx.Response.WriteAsync("ok");
        }
        catch (Exception) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("database not answering");
        }
    });
}

/// <summary>
/// Lets systemd restart MediaRiffle when it hangs. With <c>WatchdogSec=</c> in the unit, systemd expects a keep-alive
/// every so often and kills and restarts the service when none comes. This sends one only while the server answers
/// its own /healthz, so a server that still runs but no longer answers (a starved thread pool, a stuck database, a
/// share that stopped responding) is restarted instead of staying up behind a 503. A server that is so stuck that this
/// can't run sends none either. Does nothing when systemd isn't asking (Windows, or a unit without WatchdogSec).
/// </summary>
public sealed class Watchdog(IServer server, ILogger<Watchdog> log) : BackgroundService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // The address is this machine's own; a certificate for another name is not a reason to call it down
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    }) { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var socket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(socket) ||
            !long.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out var usec) || usec <= 0)
            return;
        // Several times in the time allowed, so one slow answer doesn't cost a restart
        var every = TimeSpan.FromMicroseconds(usec / 4.0);
        log.LogInformation("Systemd watchdog on: restarts MediaRiffle if it stops answering for {Seconds:0} s", usec / 1e6);

        using var timer = new PeriodicTimer(every);
        int failing = 0;
        try
        {
            do
            {
                var problem = await CheckAsync(stoppingToken);
                if (problem is null)
                {
                    if (failing > 0) log.LogInformation("The server is answering again");
                    failing = 0;
                    Notify(socket, "WATCHDOG=1");
                }
                else if (failing++ == 0)
                    log.LogError("The server isn't answering its health check ({Problem}); systemd restarts it if this goes on", problem);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Null when /healthz answered ok on one of the server's own addresses, else what was wrong.</summary>
    private async Task<string?> CheckAsync(CancellationToken ct)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        // A wildcard address (http://+:5080, http://[::]:5080) is reached on this machine as localhost
        var url = addresses.Select(a => a.Replace("://+", "://localhost").Replace("://*", "://localhost")
                                         .Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"))
                           .OrderBy(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (url is null) return "no address to ask";
        try
        {
            using var response = await Http.GetAsync(url.TrimEnd('/') + Health.Path, ct);
            return response.IsSuccessStatusCode ? null : $"answered {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ex is TaskCanceledException ? "no answer in 10 s" : ex.Message;
        }
    }

    /// <summary>One datagram to systemd's notify socket (a name starting with @ is in the abstract namespace).</summary>
    private void Notify(string socketPath, string message)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            s.SendTo(Encoding.ASCII.GetBytes(message), new UnixDomainSocketEndPoint(socketPath.StartsWith('@') ? "\0" + socketPath[1..] : socketPath));
        }
        catch (Exception ex) { log.LogWarning("Couldn't tell systemd: {Error}", ex.Message); }
    }
}
