using Censor.Core;

namespace Censor.Core.Tests;

public sealed class SubtitleScheduleTextTests
{
    [Fact]
    public void ImportsSrtCuesAndText()
    {
        const string text = """
            1
            00:00:01,250 --> 00:00:02,500
            first
            second

            2
            00:00:04,000 --> 00:00:05,000
            another
            """;

        var result = SubtitleScheduleText.Import(text, SubtitleFormat.Srt);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                new CensorInterval(1_250, 2_500, "first\nsecond"),
                new CensorInterval(4_000, 5_000, "another"),
            ],
            result.Document!.Intervals);
    }

    [Fact]
    public void ImportsWebVttIdentifiersSettingsAndShortTimestamps()
    {
        const string text = """
            WEBVTT - Censor
            Kind: captions

            NOTE ignored block
            not a cue

            scene-1
            00:01.250 --> 00:02.500 align:start
            blur this
            """;

        var result = SubtitleScheduleText.Import(text, SubtitleFormat.WebVtt);

        Assert.True(result.IsSuccess);
        Assert.Equal(new CensorInterval(1_250, 2_500, "blur this"), result.Document!.Intervals.Single());
    }

    [Fact]
    public void ImportsCommonDecimalSeparatorsAndShortFractions()
    {
        var srt = SubtitleScheduleText.Import(
            "1\n00:00:01.25 --> 00:00:02,5\nblur\n",
            SubtitleFormat.Srt);
        var webVtt = SubtitleScheduleText.Import(
            "WEBVTT\n\n00:01,2 --> 00:02.50\nblur\n",
            SubtitleFormat.WebVtt);

        Assert.True(srt.IsSuccess);
        Assert.True(webVtt.IsSuccess);
        Assert.Equal(new CensorInterval(1_250, 2_500, "blur"), srt.Document!.Intervals.Single());
        Assert.Equal(new CensorInterval(1_200, 2_500, "blur"), webVtt.Document!.Intervals.Single());
    }

    [Fact]
    public void RejectsWholeImportWhenAnyCueIsInvalid()
    {
        const string text = "1\n00:00:01,000 --> 00:00:02,000\nvalid\n\n2\nbroken --> cue\ninvalid\n";

        var result = SubtitleScheduleText.Import(text, SubtitleFormat.Srt);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Document);
        Assert.Contains(result.Diagnostics, item => item.Line == 6);
    }

    [Theory]
    [InlineData(SubtitleFormat.Srt)]
    [InlineData(SubtitleFormat.WebVtt)]
    public void ExportCanBeImportedBack(SubtitleFormat format)
    {
        var document = new ScheduleDocument(
            new ScheduleMetadata(),
            [new CensorInterval(1_250, 2_500, "line one\nline two")],
            []);

        var text = SubtitleScheduleText.Export(document, format);
        var imported = SubtitleScheduleText.Import(text, format);

        Assert.True(imported.IsSuccess);
        Assert.Equal(new CensorInterval(1_250, 2_500, "line one line two"), imported.Document!.Intervals.Single());
    }

    [Fact]
    public void RejectsWebVttWithoutHeader()
    {
        var result = SubtitleScheduleText.Import(
            "00:01.000 --> 00:02.000\ntext",
            SubtitleFormat.WebVtt);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ImportHonorsConfiguredByteAndIntervalLimits()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:02,000
            one

            2
            00:00:03,000 --> 00:00:04,000
            two
            """;

        var tooMany = SubtitleScheduleText.Import(
            text,
            SubtitleFormat.Srt,
            maxIntervals: 1);
        var tooLarge = SubtitleScheduleText.Import(
            text,
            SubtitleFormat.Srt,
            maxTextFileBytes: 10);

        Assert.False(tooMany.IsSuccess);
        Assert.Contains(tooMany.Diagnostics, item =>
            item.Message.Contains("больше 1 интервалов", StringComparison.Ordinal));
        Assert.False(tooLarge.IsSuccess);
        Assert.Contains(tooLarge.Diagnostics, item =>
            item.Message.Contains("10 байт", StringComparison.Ordinal));
    }
}
