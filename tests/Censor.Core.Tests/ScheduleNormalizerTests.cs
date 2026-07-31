using Censor.Core;
using FsCheck;
using FsCheck.Xunit;

namespace Censor.Core.Tests;

public sealed class ScheduleNormalizerTests
{
    [Fact]
    public void AppliesTimingOptionsClampsAndMerges()
    {
        CensorInterval[] intervals =
        [
            new(160, 200),
            new(10, 100),
        ];

        var result = ScheduleNormalizer.Normalize(
            intervals,
            new(LeadInMs: 50, LeadOutMs: 10, OffsetMs: -20, MergeGapMs: 20));

        Assert.Equal([new NormalizedInterval(0, 190)], result);
    }

    [Fact]
    public void DropsIntervalsThatEndBeforeZero()
    {
        var result = ScheduleNormalizer.Normalize(
            [new CensorInterval(100, 200)],
            new(LeadInMs: 0, LeadOutMs: 0, OffsetMs: -1_000, MergeGapMs: 0));

        Assert.Empty(result);
    }

    [Fact]
    public void CheckedArithmeticRejectsOverflow()
    {
        Assert.Throws<OverflowException>(() =>
            ScheduleNormalizer.Normalize(
                [new CensorInterval(long.MaxValue - 1, long.MaxValue)],
                new(LeadInMs: 0, LeadOutMs: 1, OffsetMs: 0, MergeGapMs: 0)));
    }

    [Property(MaxTest = 100)]
    public bool OutputIsSortedAndSeparated(
        NonNegativeInt firstStart,
        PositiveInt firstLength,
        NonNegativeInt gap,
        PositiveInt secondLength)
    {
        var start = firstStart.Get % 1_000_000;
        var firstEnd = start + (firstLength.Get % 10_000) + 1L;
        var secondStart = firstEnd + (gap.Get % 200);
        CensorInterval[] input =
        [
            new(secondStart, secondStart + (secondLength.Get % 10_000) + 1L),
            new(start, firstEnd),
        ];

        var result = ScheduleNormalizer.Normalize(input, new(0, 0, 0, 50));

        return result.Zip(result.Skip(1))
            .All(pair => pair.Second.StartMs - pair.First.EndMs > 50);
    }
}
