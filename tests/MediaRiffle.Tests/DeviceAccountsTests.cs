using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaRiffle.Tests;

public class DeviceAccountsTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();

    // A request carrying the cookie a previous response set
    private static HttpContext Next(HttpContext? previous = null)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        var set = previous?.Response.Headers.SetCookie.ToString();
        if (!string.IsNullOrEmpty(set)) ctx.Request.Headers.Cookie = set.Split(';')[0];
        return ctx;
    }

    private static long Ago(TimeSpan t) => DateTimeOffset.UtcNow.Add(-t).ToUnixTimeSeconds();

    [Fact]
    public void TheListComesBackNewestFirstWithoutExpiredOnes()
    {
        var first = Next();
        DeviceAccounts.Write(first, [
            new DeviceAccount("old", "s1", Ago(TimeSpan.FromDays(20))),     // unused past 14 days: gone
            new DeviceAccount("bob", "s2", Ago(TimeSpan.FromDays(2))),
            new DeviceAccount("ann", "s3", Ago(TimeSpan.FromMinutes(1))),
        ]);
        var list = DeviceAccounts.Read(Next(first));
        Assert.Equal(["ann", "bob"], list.Select(a => a.UserId));
        Assert.Equal("s2", list[1].Stamp);
    }

    [Fact]
    public void AnAlteredCookieIsNoAccountsAtAll()
    {
        var ctx = Next();
        ctx.Request.Headers.Cookie = $"{DeviceAccounts.CookieName}=CfDJ8-not-really";
        Assert.Empty(DeviceAccounts.Read(ctx));
    }

    [Fact]
    public void ForgettingTheLastAccountDropsTheCookie()
    {
        var first = Next();
        DeviceAccounts.Write(first, [new DeviceAccount("ann", "s", Ago(TimeSpan.Zero))]);
        var second = Next(first);
        DeviceAccounts.Forget(second, "ann");
        Assert.Contains("expires=Thu, 01 Jan 1970", second.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCookieIsHttpOnlyAndLax()
    {
        var ctx = Next();
        DeviceAccounts.Write(ctx, [new DeviceAccount("ann", "s", Ago(TimeSpan.Zero))]);
        var header = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.Contains("httponly", header);
        Assert.Contains("samesite=lax", header);   // it must come back from Google's sign-in
    }
}
