using System.Globalization;
using Censor.Core;

namespace Censor.Core.Tests;

public sealed class FilterCompilerTests
{
    [Fact]
    public void CompilesOnlyNumericIntervalsWithStableLabel()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var plan = FilterCompiler.Compile(
                [new(1_250, 2_500), new(4_000, 5_001)],
                new(Sigma: 30, Steps: 2));

            var chunk = Assert.Single(plan.Chunks);
            Assert.Equal("@censor_blur_000", chunk.Label);
            Assert.Equal(
                "@censor_blur_000:lavfi=[gblur=sigma=30:steps=2:enable='(gte(t,1.250)*lt(t,2.500))+(gte(t,4.000)*lt(t,5.001))']",
                chunk.Filter);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void SplitsLargePlansIntoStableChunks()
    {
        var intervals = Enumerable.Range(0, 501)
            .Select(index => new NormalizedInterval(index * 2_000L, (index * 2_000L) + 1_000))
            .ToArray();

        var plan = FilterCompiler.Compile(intervals, new());

        Assert.Equal(2, plan.Chunks.Count);
        Assert.Equal("@censor_blur_001", plan.Chunks[1].Label);
        Assert.Contains("gte(t,1000.000)", plan.Chunks[1].Filter, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMoreThanFiftyChunks()
    {
        var intervals = Enumerable.Range(0, (FilterCompiler.MaxIntervalsPerChunk * FilterCompiler.MaxFilters) + 1)
            .Select(index => new NormalizedInterval(index * 2L, (index * 2L) + 1))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => FilterCompiler.Compile(intervals, new()));
    }

    [Theory]
    [InlineData(0.001, 2)]
    [InlineData(1_025, 2)]
    [InlineData(30, 0)]
    [InlineData(30, 7)]
    public void RejectsUnsupportedBlurSettings(double sigma, int steps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FilterCompiler.Compile([new(1_000, 2_000)], new(sigma, steps)));
    }
}
