using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class SubtitleLanguageTests
{
    private static ProbeStream Sub(int index, string codec = "subrip", string? lang = "eng", string? title = null, bool forced = false) =>
        new(index, StreamType.Subtitle, codec, null, null, 0, 0, 0, lang, title, false, forced);

    [Fact]
    public void PicksTheTrackInThatLanguage_WhateverTheSpelling()
    {
        Assert.Equal("s4", SubtitleLanguages.Pick([Sub(3, lang: "spa"), Sub(4, lang: "eng")], "en"));
        Assert.Equal("s3", SubtitleLanguages.Pick([Sub(3, lang: "EN"), Sub(4, lang: "eng")], "en"));
        Assert.Equal("s3", SubtitleLanguages.Pick([Sub(3, lang: "fre")], "fr"));
    }

    [Fact]
    public void NothingWhenOffOrNoTrackMatches()
    {
        Assert.Null(SubtitleLanguages.Pick([Sub(3)], null));
        Assert.Null(SubtitleLanguages.Pick([Sub(3)], "xx"));
        Assert.Null(SubtitleLanguages.Pick([Sub(3, lang: "spa"), Sub(4, lang: null), Sub(5, lang: "und")], "en"));
    }

    [Fact]
    public void LeavesOutForcedAndCommentaryTracks()
    {
        Assert.Null(SubtitleLanguages.Pick([Sub(3, forced: true), Sub(4, title: "Director's Commentary")], "en"));
        Assert.Equal("s5", SubtitleLanguages.Pick([Sub(3, forced: true), Sub(5)], "en"));
    }

    [Fact]
    public void PrefersFullSubtitlesOverHearingImpairedOnes_AndTextOverPgs()
    {
        Assert.Equal("s4", SubtitleLanguages.Pick([Sub(3, title: "English SDH"), Sub(4)], "en"));
        Assert.Equal("s3", SubtitleLanguages.Pick([Sub(3, title: "SDH")], "en"));
        Assert.Equal("s4", SubtitleLanguages.Pick([Sub(3, "hdmv_pgs_subtitle"), Sub(4)], "en"));
        Assert.Equal("s3", SubtitleLanguages.Pick([Sub(3, "hdmv_pgs_subtitle")], "en"));
    }

    [Fact]
    public void NeverPicksAPictureTrackThatWouldHaveToBeBurnedIn() =>
        Assert.Null(SubtitleLanguages.Pick([Sub(3, "dvd_subtitle")], "en"));

    [Fact]
    public void OnlyKnownLanguagesAreAccepted()
    {
        Assert.Equal("en", SubtitleLanguages.Normalize(" EN "));
        Assert.Null(SubtitleLanguages.Normalize("klingon"));
        Assert.Null(SubtitleLanguages.Normalize(""));
    }
}
