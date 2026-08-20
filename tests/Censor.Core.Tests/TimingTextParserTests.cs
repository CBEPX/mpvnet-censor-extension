using Censor.Core;

namespace Censor.Core.Tests;

public sealed class TimingTextParserTests
{
    [Fact]
    public void ParsesRangePointAndDescriptionWithoutInventingAnEnd()
    {
        var range = TimingTextParser.Parse(
            "2031",
            "00:12:03-00:12:08 (спорная сцена)",
            "approved",
            2);
        var point = TimingTextParser.Parse(
            "2032",
            "00:32:22 (вид глотки изнутри)",
            "approved",
            -1);

        Assert.Equal(
            [
                new TimingCandidate(
                    "2031",
                    0,
                    TimingCandidateKind.Interval,
                    723_000,
                    728_000,
                    "00:12:03-00:12:08 (спорная сцена)",
                    "спорная сцена",
                    "approved",
                    2),
            ],
            range);
        Assert.Equal(TimingCandidateKind.Point, Assert.Single(point).Kind);
        Assert.Equal(1_942_000, point[0].StartMs);
        Assert.Null(point[0].EndMs);
        Assert.Equal("вид глотки изнутри", point[0].Note);
    }

    [Fact]
    public void ParsesSeveralRangesAndMinuteOnlyTimestamp()
    {
        var candidates = TimingTextParser.Parse(
            "42",
            "1:02–1:05; 01:02:03.250 --> 01:02:04,500 описание");

        Assert.Equal(2, candidates.Count);
        Assert.Collection(
            candidates,
            first =>
            {
                Assert.Equal(TimingCandidateKind.Interval, first.Kind);
                Assert.Equal(62_000, first.StartMs);
                Assert.Equal(65_000, first.EndMs);
                Assert.Equal(0, first.FragmentIndex);
                Assert.Null(first.Note);
            },
            second =>
            {
                Assert.Equal(TimingCandidateKind.Interval, second.Kind);
                Assert.Equal(3_723_250, second.StartMs);
                Assert.Equal(3_724_500, second.EndMs);
                Assert.Equal(1, second.FragmentIndex);
                Assert.Equal("описание", second.Note);
            });
    }

    [Theory]
    [InlineData("Чисто")]
    [InlineData("  чисто.  ")]
    public void PreservesCleanClaimAsPreviewOnlyCandidate(string text)
    {
        var candidate = Assert.Single(TimingTextParser.Parse("7", text));

        Assert.Equal(TimingCandidateKind.CleanClaim, candidate.Kind);
        Assert.Null(candidate.StartMs);
        Assert.Null(candidate.EndMs);
        Assert.Equal(text, candidate.RawText);
    }

    [Theory]
    [InlineData("примерно в середине")]
    [InlineData("00:20:10-00:19:59")]
    [InlineData("00:99:00")]
    public void InvalidTextIsPreservedInsteadOfSilentlyDropped(string text)
    {
        var candidate = Assert.Single(TimingTextParser.Parse("8", text));

        Assert.Equal(TimingCandidateKind.Unparsed, candidate.Kind);
        Assert.Equal(text, candidate.RawText);
    }

    [Fact]
    public void RejectsOversizedOrMissingSourceInput()
    {
        Assert.Throws<ArgumentException>(() => TimingTextParser.Parse("", "00:01"));
        Assert.Throws<ArgumentException>(() =>
            TimingTextParser.Parse("1", new string('x', TimingTextParser.MaxTextLength + 1)));
    }

    [Fact]
    public void AcceptsTheMaximumTimestampWithoutSalvagingAnOverflowingOne()
    {
        var maximum = Assert.Single(TimingTextParser.Parse("9", "99:59:59.999"));
        var overflow = Assert.Single(TimingTextParser.Parse("10", "100:00:00"));

        Assert.Equal(TimingCandidateKind.Point, maximum.Kind);
        Assert.Equal(ScheduleText.MaxTimestampMs, maximum.StartMs);
        Assert.Equal(TimingCandidateKind.Unparsed, overflow.Kind);
    }

    [Fact]
    public void KeepsValidFragmentsWhenALaterRangeIsInverted()
    {
        var candidates = TimingTextParser.Parse(
            "11",
            "10:00-11:00; 20:00-19:00");

        Assert.Collection(
            candidates,
            first => Assert.Equal(TimingCandidateKind.Interval, first.Kind),
            second => Assert.Equal(TimingCandidateKind.Unparsed, second.Kind));
    }

    [Fact]
    public void KeepsValidFragmentsWhenALaterTimestampIsInvalid()
    {
        var candidates = TimingTextParser.Parse(
            "12",
            "10:00-11:00; 9999:59");

        Assert.Collection(
            candidates,
            first => Assert.Equal(TimingCandidateKind.Interval, first.Kind),
            second => Assert.Equal(TimingCandidateKind.Unparsed, second.Kind));
    }

    [Fact]
    public void AssignsLeadingProseToTheFollowingSemicolonSeparatedRange()
    {
        var candidates = TimingTextParser.Parse(
            "13",
            "сцена A 01:00-02:00; сцена B 03:00-04:00");

        Assert.Collection(
            candidates,
            first => Assert.Equal("сцена A", first.Note),
            second => Assert.Equal("сцена B", second.Note));
    }

    [Fact]
    public void PreservesUnicodeFractionAsUnparsedTextInsteadOfThrowing()
    {
        var candidate = Assert.Single(TimingTextParser.Parse("14", "00:12:03.٥"));

        Assert.Equal(TimingCandidateKind.Unparsed, candidate.Kind);
        Assert.Equal("00:12:03.٥", candidate.RawText);
    }

    [Fact]
    public void KeepsParenthesesThatDoNotEncloseTheWholeNote()
    {
        var candidate = Assert.Single(TimingTextParser.Parse(
            "15",
            "00:12:03 (начало) середина (конец)"));

        Assert.Equal("(начало) середина (конец)", candidate.Note);
    }

    [Fact]
    public void KeepsFragmentNumbersUniqueWithinOneSourceEntry()
    {
        var candidates = TimingTextParser.Parse(
            "16",
            "00:10 отметка; 01:00-02:00 первая; 03:00 точка; 04:00-05:00 вторая");

        Assert.Equal(
            Enumerable.Range(0, candidates.Count),
            candidates.Select(candidate => candidate.FragmentIndex));
    }

    [Fact]
    public void KeepsOnlyTheMatchingRawTextInEachFragment()
    {
        var candidates = TimingTextParser.Parse(
            "17",
            "01:00-01:05; 02:00-02:05 вторая");

        Assert.Collection(
            candidates,
            first => Assert.Equal("01:00-01:05", first.RawText),
            second => Assert.Equal("02:00-02:05 вторая", second.RawText));
    }

    [Fact]
    public void RemovesBidiControlsFromExternalText()
    {
        var candidate = Assert.Single(TimingTextParser.Parse(
            "18",
            "01:00-01:05 начало\u202EABC"));

        Assert.DoesNotContain('\u202E', candidate.RawText);
        Assert.DoesNotContain('\u202E', candidate.Note!);
    }

    [Fact]
    public void KeepsHyphensAndDashesInsideTheDescription()
    {
        var candidate = Assert.Single(TimingTextParser.Parse(
            "19",
            "00:12:03-00:12:08 сцена из-за угла — кто-то"));

        Assert.Equal("сцена из-за угла — кто-то", candidate.Note);
    }
}
