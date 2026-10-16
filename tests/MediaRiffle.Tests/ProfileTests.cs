using System.Text.Json;
using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;

namespace MediaRiffle.Tests;

public class ProfileTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void EveryoneStartsWithABuiltInPictureTheirAccountPicks()
    {
        var a = Avatars.DefaultFor("4b25ec85-adc0-4a23-be91-07e8aa1be730");
        Assert.InRange(a, 0, Avatars.BuiltIn - 1);
        // The same every time (not string.GetHashCode, which changes between runs)
        Assert.Equal(a, Avatars.DefaultFor("4b25ec85-adc0-4a23-be91-07e8aa1be730"));
        // Different people mostly differ
        var many = Enumerable.Range(0, 200).Select(i => Avatars.DefaultFor(Guid.NewGuid().ToString())).Distinct().Count();
        Assert.True(many > Avatars.BuiltIn / 2);
    }

    [Fact]
    public void APictureIsABuiltInOneOrAPhoto()
    {
        Assert.Equal("""{"emoji":7}""", Json(Avatars.Of(new AppUser { Id = "u", Avatar = "emoji:7" })));
        Assert.Equal("""{"photo":"/api/avatars/u?v=123"}""", Json(Avatars.Of(new AppUser { Id = "u", Avatar = "photo:123" })));
        // Out of range, or nothing chosen: the account's own built-in one
        Assert.Equal(Json(new { emoji = Avatars.DefaultFor("u") }), Json(Avatars.Of(new AppUser { Id = "u", Avatar = "emoji:99" })));
        Assert.Equal(Json(new { emoji = Avatars.DefaultFor("u") }), Json(Avatars.Of(new AppUser { Id = "u" })));
    }

    [Fact]
    public void OnlyJpegsAreTakenAsPhotos()
    {
        Assert.True(Avatars.IsJpeg([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
        Assert.False(Avatars.IsJpeg("<svg onload=…>"u8));
        Assert.False(Avatars.IsJpeg([0x89, 0x50, 0x4E, 0x47]));   // PNG
    }

    [Theory]
    [InlineData(1300, 1320, true)]     // 22-minute episode, 20 s left
    [InlineData(1200, 1320, true)]     // 2 minutes left: credits
    [InlineData(900, 1320, false)]     // 7 minutes left
    [InlineData(6700, 7200, true)]     // movie, 8 minutes left: the last 8%
    [InlineData(6000, 7200, false)]    // movie, 20 minutes left
    [InlineData(280, 300, true)]       // a 5-minute clip: the last 8% only
    [InlineData(200, 300, false)]
    public void StoppingAtTheCreditsCountsAsWatched(double position, double duration, bool done) =>
        Assert.Equal(done, PlaybackEndpoints.NearlyDone(position, duration));
}
