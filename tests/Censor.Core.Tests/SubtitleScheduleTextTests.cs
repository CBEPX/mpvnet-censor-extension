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
}
