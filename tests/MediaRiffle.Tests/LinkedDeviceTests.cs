using System.Net;
using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Http;

namespace MediaRiffle.Tests;

public class LinkedDeviceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DevicePairings Pairings, Clock Clock) New()
    {
        var clock = new Clock();
        return (new DevicePairings(clock), clock);
    }

    // ── Pairing ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACodeIsEightUnmistakableCharactersShownWithADash()
    {
        var (pairings, _) = New();
        var p = pairings.Start("Xbox 360", "192.168.0.50")!;
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ2-9]{8}$", p.UserCode);
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ2-9]{4}-[BCDFGHJKLMNPQRSTVWXZ2-9]{4}$", DevicePairings.Display(p.UserCode));
        Assert.True(p.DeviceCode.Length >= 43);   // 256 bits
    }

    [Fact]
    public void TheCodeIsFoundHoweverItIsTyped()
    {
        var (pairings, _) = New();
        var p = pairings.Start("Xbox 360", null)!;
        var typed = " " + DevicePairings.Display(p.UserCode).ToLowerInvariant() + " ";
        Assert.Same(p, pairings.Find(typed));
        Assert.Null(pairings.Find("BCDF-2345X"));
        Assert.Null(pairings.Find(null));
    }

    [Fact]
    public void TheDeviceWaitsUntilTheCodeIsEnteredThenGetsItOnce()
    {
        var (pairings, _) = New();
        var p = pairings.Start("Xbox 360", null)!;
        Assert.Equal(PairingState.Waiting, pairings.Poll(p.DeviceCode).State);
        Assert.True(pairings.Approve(p.UserCode, "user-1"));
        var linked = pairings.Poll(p.DeviceCode);
        Assert.Equal((PairingState.Linked, "user-1", "Xbox 360"), linked);
        Assert.Equal(PairingState.Gone, pairings.Poll(p.DeviceCode).State);
    }

    [Fact]
    public void ACodeCanOnlyBeEnteredOnce()
    {
        var (pairings, _) = New();
        var p = pairings.Start("Xbox 360", null)!;
        Assert.True(pairings.Approve(p.UserCode, "user-1"));
        Assert.False(pairings.Approve(p.UserCode, "user-2"));   // not taken over by someone else
        Assert.Null(pairings.Find(p.UserCode));
        Assert.Equal("user-1", pairings.Poll(p.DeviceCode).UserId);
    }

    [Fact]
    public void ACodeRunsOut()
    {
        var (pairings, clock) = New();
        var p = pairings.Start("Xbox 360", null)!;
        clock.Now += DevicePairings.Lifetime;
        Assert.Null(pairings.Find(p.UserCode));
        Assert.False(pairings.Approve(p.UserCode, "user-1"));
        Assert.Equal(PairingState.Gone, pairings.Poll(p.DeviceCode).State);
    }

    [Fact]
    public void AnEnteredCodeStillRunsOutIfTheDeviceNeverAsks()
    {
        var (pairings, clock) = New();
        var p = pairings.Start("Xbox 360", null)!;
        Assert.True(pairings.Approve(p.UserCode, "user-1"));
        clock.Now += DevicePairings.Lifetime;
        Assert.Equal(PairingState.Gone, pairings.Poll(p.DeviceCode).State);
    }

    [Fact]
    public void TheDeviceCodeIsNeededToAsk()
    {
        var (pairings, _) = New();
        var p = pairings.Start("Xbox 360", null)!;
        pairings.Approve(p.UserCode, "user-1");
        Assert.Equal(PairingState.Gone, pairings.Poll(p.UserCode).State);   // the code shown isn't the secret
        Assert.Equal(PairingState.Gone, pairings.Poll(null).State);
    }

    [Fact]
    public void OnlySoManyCodesWaitAtOnce()
    {
        var (pairings, clock) = New();
        for (var i = 0; i < DevicePairings.MaxWaiting; i++) Assert.NotNull(pairings.Start("TV", null));
        Assert.Null(pairings.Start("TV", null));
        clock.Now += DevicePairings.Lifetime;   // run out: room again
        Assert.NotNull(pairings.Start("TV", null));
    }

    [Theory]
    [InlineData("Xbox 360", "Xbox 360")]
    [InlineData("  Living room\u0007\n ", "Living room")]
    [InlineData("", "Device")]
    [InlineData(null, "Device")]
    public void DeviceNamesAreCleanedUp(string? given, string shown) => Assert.Equal(shown, DevicePairings.CleanName(given));

    [Fact]
    public void ALongNameIsCut() => Assert.Equal(LinkedDevice.MaxName, DevicePairings.CleanName(new string('x', 100)).Length);

    // ── Tokens ──────────────────────────────────────────────────────────────

    private static HttpContext Request(string? authorization = null, bool deviceEndpoint = false, string? address = null, bool https = false)
    {
        var ctx = new DefaultHttpContext();
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;
        ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            deviceEndpoint ? new EndpointMetadataCollection(DeviceAccess.Instance) : EndpointMetadataCollection.Empty, "test"));
        if (address is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(address);
        ctx.Request.Scheme = https ? "https" : "http";
        return ctx;
    }

    [Theory]
    [InlineData("Bearer abc123", "abc123")]
    [InlineData("bearer  abc123 ", "abc123")]
    [InlineData("Bearer ", null)]
    [InlineData("Basic dXNlcjpwYXNz", null)]
    public void TheTokenComesFromTheAuthorizationHeader(string header, string? token) =>
        Assert.Equal(token, DeviceTokens.BearerToken(Request(header).Request));

    [Fact]
    public void ARequestWithATokenIsTheDeviceSchemes()
    {
        Assert.Equal(DeviceTokens.Scheme, DeviceTokens.SelectScheme(Request("Bearer abc")));
        Assert.Null(DeviceTokens.SelectScheme(Request()));   // a browser's: the cookie's
    }

    [Fact]
    public void ADeviceOnlyReachesEndpointsOpenedToDevices()
    {
        Assert.True(DeviceTokens.Allowed(Request("Bearer abc", deviceEndpoint: true)));
        Assert.False(DeviceTokens.Allowed(Request("Bearer abc", deviceEndpoint: false)));   // Admin, account settings, …
        Assert.True(DeviceTokens.Allowed(Request(null, deviceEndpoint: false)));            // browsers aren't affected
    }

    [Theory]
    [InlineData("192.168.0.50", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.9", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.0.50", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("2001:db8::1", false)]
    public void PlainHttpIsOnlyForTheLocalNetwork(string address, bool allowed)
    {
        Assert.Equal(allowed, DeviceTokens.TransportAllowed(Request(address: address)));
        Assert.True(DeviceTokens.TransportAllowed(Request(address: address, https: true)));   // HTTPS from anywhere
    }

    [Fact]
    public void ADeviceIsKnownByItsClaim()
    {
        var device = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DeviceTokens.DeviceClaim, "42")], DeviceTokens.Scheme));
        Assert.Equal(42, DeviceTokens.DeviceId(device));
        Assert.Null(DeviceTokens.DeviceId(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u")], "cookie"))));
    }

    [Fact]
    public void TokensAreLongAndNeverRepeat()
    {
        var a = DeviceTokens.NewToken();
        Assert.True(a.Length >= 43);
        Assert.NotEqual(a, DeviceTokens.NewToken());
    }
}
