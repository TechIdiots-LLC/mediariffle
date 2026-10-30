using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;

namespace MediaRiffle.Tests;

public class RequestTests
{
    private static string? Check(string? title, int? year = 1999, string? link = null, RequestKind kind = RequestKind.Movie) =>
        RequestEndpoints.Validate(new RequestEndpoints.RequestBody(title, year, kind, null, link));

    [Fact]
    public void ATitleAloneIsEnough() => Assert.Null(Check("The Matrix", year: null));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ATitleIsNeeded(string? title) => Assert.NotNull(Check(title));

    [Fact]
    public void ALongTitleIsRefused() => Assert.NotNull(Check(new string('x', MediaRequest.MaxTitle + 1)));

    [Theory]
    [InlineData(1700)]
    [InlineData(3500)]
    public void ASillyYearIsRefused(int year) => Assert.NotNull(Check("Old", year));

    [Fact]
    public void AKindThatIsntOneIsRefused() => Assert.NotNull(Check("X", kind: (RequestKind)9));

    [Theory]
    [InlineData("https://www.imdb.com/title/tt0133093/", true)]
    [InlineData("http://shop.example/dvd", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("not a link", false)]
    public void OnlyWebAddressesAreLinks(string link, bool ok) => Assert.Equal(ok, Check("X", link: link) is null);
}
