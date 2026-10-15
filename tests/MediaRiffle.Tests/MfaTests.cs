using System.Security.Claims;
using MediaRiffle.Server.Api;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;

namespace MediaRiffle.Tests;

public class MfaTests
{
    private static ClaimsPrincipal With(string type, DateTimeOffset at) =>
        new(new ClaimsIdentity([new Claim(type, at.ToUnixTimeSeconds().ToString())], "test"));

    [Fact]
    public void AVerificationLastsItsHours()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(Mfa.IsFresh(With(Mfa.VerifiedClaim, now.AddHours(-11)), TimeSpan.FromHours(12), now));
        Assert.False(Mfa.IsFresh(With(Mfa.VerifiedClaim, now.AddHours(-13)), TimeSpan.FromHours(12), now));
        // A mark from the future (a clock gone wrong) doesn't count
        Assert.False(Mfa.IsFresh(With(Mfa.VerifiedClaim, now.AddHours(1)), TimeSpan.FromHours(12), now));
        Assert.False(Mfa.IsFresh(new ClaimsPrincipal(new ClaimsIdentity()), TimeSpan.FromHours(12), now));
        // A confirmed password isn't a verification
        Assert.False(Mfa.IsFresh(With(Mfa.PasswordClaim, now), TimeSpan.FromHours(12), now));
    }

    [Fact]
    public void AConfirmedPasswordLastsAQuarterHour()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(Mfa.PasswordConfirmed(With(Mfa.PasswordClaim, now.AddMinutes(-10)), now));
        Assert.False(Mfa.PasswordConfirmed(With(Mfa.PasswordClaim, now.AddMinutes(-16)), now));
    }

    // The cookie is rebuilt from the database every minute; the marks must come along or Admin would ask again
    [Fact]
    public async Task MarksSurviveTheCookieRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(Mfa.VerifiedClaim, now.ToUnixTimeSeconds().ToString()), new Claim("other", "x")], "test"));
        var rebuilt = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));
        var ctx = new SecurityStampRefreshingPrincipalContext { CurrentPrincipal = current, NewPrincipal = rebuilt };
        await Mfa.KeepMarks(ctx);
        Assert.True(Mfa.IsFresh(rebuilt, TimeSpan.FromHours(12)));
        Assert.Null(rebuilt.FindFirst("other"));
    }

    [Fact]
    public void AuthenticatorAppsGetALinkAKeyToTypeAndAQrCode()
    {
        Assert.Equal("otpauth://totp/MediaRiffle:ann%20b?secret=ABCDEFGH&issuer=MediaRiffle&digits=6",
            MfaEndpoints.AuthenticatorUri("ann b", "ABCDEFGH"));
        Assert.Equal("abcd efgh ij", MfaEndpoints.FormatKey("ABCDEFGHIJ"));
        var qr = MfaEndpoints.QrSvgDataUri("otpauth://totp/x?secret=ABC");
        Assert.StartsWith("data:image/svg+xml;base64,", qr);
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(qr["data:image/svg+xml;base64,".Length..])));
    }
}
