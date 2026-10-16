using System.Net;
using System.Threading.RateLimiting;
using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Security;
using MediaRiffle.Server.Streaming;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Runs as a systemd service on Linux and under IIS (ASP.NET Core Module) or as a Windows service on Windows
builder.Host.UseSystemd();
builder.Host.UseWindowsService();
// As a Windows service, warnings and errors go to the Application event log under the "MediaRiffle" source, which the
// installer registers (the service's own account isn't allowed to create one)
if (OperatingSystem.IsWindows())
    builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(s => { s.SourceName = "MediaRiffle"; s.LogName = "Application"; });

// Settings live outside the program folder, so an upgrade (replacing the published files) can't overwrite them:
// /etc/mediariffle/appsettings.json on Linux, %ProgramData%\MediaRiffle\appsettings.json on Windows.
// Environment variables (MediaRiffle__Libraries__0__Path=…) still override both.
var systemConfig = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaRiffle", "appsettings.json")
    : "/etc/mediariffle/appsettings.json";
builder.Configuration.AddJsonFile(systemConfig, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<MediaRiffleOptions>(builder.Configuration.GetSection(MediaRiffleOptions.Section));
var options = builder.Configuration.GetSection(MediaRiffleOptions.Section).Get<MediaRiffleOptions>() ?? new MediaRiffleOptions();
Directory.CreateDirectory(options.ResolvedDataDirectory);
Directory.CreateDirectory(options.ImageDirectory);
Directory.CreateDirectory(options.KeysDirectory);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={options.DatabasePath}"));

// Sign-in cookies stay valid across restarts and app pool recycles only if the keys that sign them are kept
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory))
    .SetApplicationName("MediaRiffle");

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        // Length over composition rules (NIST SP 800-63B); lockout slows guessing
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = false;
        // Passkeys (WebAuthn) are kept in AspNetUserPasskeys, which schema version 3 adds
        o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

var authentication = builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme);
authentication.AddIdentityCookies();
// Google only signs in accounts that linked it under Account; it never creates one (people join with invites)
if (options.Google.Enabled)
    authentication.AddGoogle(o =>
    {
        o.ClientId = options.Google.ClientId;
        o.ClientSecret = options.Google.ClientSecret;
        // Google's answer lands in the short-lived external cookie, read once by /api/auth/google/done
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.CallbackPath = "/signin-google";
        // Cancelling on Google's page, or an answer that can't be checked (e.g. an expired attempt), goes back
        // home with a message instead of an error page
        o.AccessDeniedPath = "/?google=cancelled";
        o.Events.OnRemoteFailure = c => { c.Response.Redirect("/?google=failed"); c.HandleResponse(); return Task.CompletedTask; };
    });
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "mediariffle.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Secure whenever the request is HTTPS, including behind a proxy that says so (X-Forwarded-Proto, trusted in
    // TrustedProxies). Always would refuse plain http://localhost:5080 outright, which the installer opens first.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    // An API answers 401/403 instead of redirecting to a login page
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    // Every sign-in keeps this device's list of accounts (Switch user) up to date, stamp included
    o.Events.OnSigningIn = DeviceAccounts.OnSigningIn;
});
// A sign-in cookie carries the user's roles; it's checked against the database (and its roles refreshed, or the user
// signed out if removed) every minute instead of every 30, so making someone an admin or a viewer takes effect quickly
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    o.ValidationInterval = TimeSpan.FromMinutes(1);
    // The rebuilt cookie keeps when the user last passed two-step verification (Admin wants a recent one)
    o.OnRefreshingPrincipal = Mfa.KeepMarks;
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin));

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "mediariffle.csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Secure whenever the request is HTTPS, including behind a proxy that says so (X-Forwarded-Proto, trusted in
    // TrustedProxies). Always would refuse plain http://localhost:5080 outright, which the installer opens first.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in, setup and invite sign-up: 10 tries a minute per address, on top of the account lockout
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("play", c => RateLimitPartition.GetFixedWindowLimiter(
        c.User.Identity?.Name ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = Math.Max(1, options.ForwardLimit);
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var proxy in options.TrustedProxies)
        if (IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
});

builder.Services.AddCors(o => o.AddPolicy("stream", p =>
    // The stream URLs carry their own unguessable session id instead of a cookie, so a Chromecast (another
    // origin) can fetch them; nothing else is shared
    p.AllowAnyOrigin().WithMethods("GET", "HEAD").WithHeaders("Range").WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length")));

// Enums by name in the API ("Shows", not 0)
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<Ffmpeg>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryScanner>());
builder.Services.AddSingleton<StreamSessionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamSessionManager>());
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<AudioService>();
builder.Services.AddSingleton<LibraryCatalog>();
builder.Services.AddSingleton<LibraryFiles>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin)) await roles.CreateAsync(new IdentityRole(Roles.Admin));
    await scope.ServiceProvider.GetRequiredService<LibraryCatalog>().InitializeAsync();
    scope.ServiceProvider.GetRequiredService<ImageService>().RemoveOldFrames();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseApiAntiforgery();

app.MapAuthEndpoints();
app.MapMfaEndpoints();
app.MapAccountSwitchEndpoints();
app.MapAdminEndpoints();
app.MapLibraryEndpoints();
app.MapListEndpoints();
app.MapHiddenEndpoints();
app.MapMusicEndpoints();
app.MapMusicCastEndpoints();
app.MapPlaybackEndpoints();
app.MapStreamEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
