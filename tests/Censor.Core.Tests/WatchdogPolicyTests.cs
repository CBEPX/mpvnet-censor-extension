using Censor.Core;

namespace Censor.Core.Tests;

public sealed class WatchdogPolicyTests
{
    private static readonly NormalizedInterval[] Intervals = [new(10_000, 20_000)];

    [Theory]
    [InlineData(6_999, false)]
    [InlineData(7_000, true)]
    [InlineData(10_000, true)]
    [InlineData(19_999, true)]
    [InlineData(20_000, false)]
    public void UsesHalfOpenBoundariesAndEarlyGuard(long positionMs, bool expected)
    {
        Assert.Equal(expected, WatchdogPolicy.IsIntervalNear(positionMs, Intervals, 3_000));
    }
}
