using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class LanguageTests
{
    private static ProbeStream Sub(int index, string codec = "subrip", string? lang = "eng", string? title = null, bool forced = false) =>
        new(index, StreamType.Subtitle, codec, null, null, 0, 0, 0, lang, title, false, forced);

    [Fact]
    public void PicksTheTrackInThatLanguage_WhateverTheSpelling()
    {
        Assert.Equal("s4", Languages.PickSubtitle([Sub(3, lang: "spa"), Sub(4, lang: "eng")], "en"));
        Assert.Equal("s3", Languages.PickSubtitle([Sub(3, lang: "EN"), Sub(4, lang: "eng")], "en"));
        Assert.Equal("s3", Languages.PickSubtitle([Sub(3, lang: "fre")], "fr"));
    }

    [Fact]
    public void NothingWhenOffOrNoTrackMatches()
    {
        Assert.Null(Languages.PickSubtitle([Sub(3)], null));
        Assert.Null(Languages.PickSubtitle([Sub(3)], "xx"));
        Assert.Null(Languages.PickSubtitle([Sub(3, lang: "spa"), Sub(4, lang: null), Sub(5, lang: "und")], "en"));
    }

    [Fact]
    public void LeavesOutForcedAndCommentaryTracks()
    {
        Assert.Null(Languages.PickSubtitle([Sub(3, forced: true), Sub(4, title: "Director's Commentary")], "en"));
        Assert.Equal("s5", Languages.PickSubtitle([Sub(3, forced: true), Sub(5)], "en"));
    }

    [Fact]
    public void PrefersFullSubtitlesOverHearingImpairedOnes_AndTextOverPgs()
    {
        Assert.Equal("s4", Languages.PickSubtitle([Sub(3, title: "English SDH"), Sub(4)], "en"));
        Assert.Equal("s3", Languages.PickSubtitle([Sub(3, title: "SDH")], "en"));
        Assert.Equal("s4", Languages.PickSubtitle([Sub(3, "hdmv_pgs_subtitle"), Sub(4)], "en"));
        Assert.Equal("s3", Languages.PickSubtitle([Sub(3, "hdmv_pgs_subtitle")], "en"));
    }

    [Fact]
    public void NeverPicksAPictureTrackThatWouldHaveToBeBurnedIn() =>
        Assert.Null(Languages.PickSubtitle([Sub(3, "dvd_subtitle")], "en"));

    [Fact]
    public void OnlyKnownLanguagesAreAccepted()
    {
        Assert.Equal("en", Languages.Normalize(" EN "));
        Assert.Null(Languages.Normalize("klingon"));
        Assert.Null(Languages.Normalize(""));
    }

    private static ProbeStream Audio(int index, string? lang, bool isDefault = false, int channels = 6, string? title = null) =>
        new(index, StreamType.Audio, "eac3", null, null, 0, 0, channels, lang, title, isDefault, false);

    [Fact]
    public void AudioSwitchesToTheLanguage_WhenTheDefaultIsAnother()
    {
        // Hindi first and default, English second (as a dual-audio web release)
        Assert.Equal(2, Languages.PickAudio([Audio(1, "hin", isDefault: true), Audio(2, "eng")], "en"));
        // No track flagged default: the first one is what plays
        Assert.Equal(2, Languages.PickAudio([Audio(1, "hin"), Audio(2, "eng")], "en"));
    }

    [Fact]
    public void AudioKeepsTheDefault_WhenItsAlreadyThatLanguage_OrNoneIs()
    {
        Assert.Null(Languages.PickAudio([Audio(1, "eng", isDefault: true), Audio(2, "eng", title: "Commentary")], "en"));
        Assert.Null(Languages.PickAudio([Audio(1, "hin", isDefault: true), Audio(2, "tam")], "en"));
        Assert.Null(Languages.PickAudio([Audio(1, "hin", isDefault: true), Audio(2, "eng")], null));
    }

    [Fact]
    public void AudioLeavesOutCommentaryAndDescription_AndPrefersMoreChannels()
    {
        Assert.Null(Languages.PickAudio([Audio(1, "hin", true), Audio(2, "eng", title: "Commentary"), Audio(3, "eng", title: "Audio Description")], "en"));
        Assert.Equal(3, Languages.PickAudio([Audio(1, "hin", true), Audio(2, "eng", channels: 2), Audio(3, "eng", channels: 6)], "en"));
    }
}
