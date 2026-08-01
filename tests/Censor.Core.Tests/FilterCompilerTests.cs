using System.Globalization;
using System.Text;
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
                BlurSettings.Moderate);

            var chunk = Assert.Single(plan.Chunks);
            Assert.Equal(BlurSettings.Moderate, plan.Blur);
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
        Assert.Contains("gblur=sigma=40:steps=2", plan.Chunks[0].Filter, StringComparison.Ordinal);
        Assert.Contains("gte(t,1000.000)", plan.Chunks[1].Filter, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInputBeyondParserSafetyLimit()
    {
        var intervals = Enumerable.Range(0, ScheduleText.MaxIntervals + 1)
            .Select(index => new NormalizedInterval(index * 2L, (index * 2L) + 1))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FilterCompiler.Compile(intervals, new()));
    }

    [Fact]
    public void ExposesNamedBlurPresets()
    {
        Assert.Equal(new(30, 2), BlurSettings.Moderate);
        Assert.Equal(new(40, 2), BlurSettings.Balanced);
        Assert.Equal(new(50, 3), BlurSettings.Maximum);
    }

    [Fact]
    public void ReadbackRequiresExactOwnedGraphAndAllowsUserFilters()
    {
        var plan = FilterCompiler.Compile([new(1_000, 2_000)], BlurSettings.Balanced);
        const string expected =
            "@censor_blur_000:lavfi=graph=%58%gblur=sigma=40:steps=2:enable='(gte(t,1.000)*lt(t,2.000))'";
        var readback = $"@user:lavfi=graph=hflip,{expected}";

        Assert.True(FilterReadback.MatchesBlurPlan(readback, plan));
        Assert.False(FilterReadback.MatchesBlurPlan(
            readback.Replace("sigma=40", "sigma=1", StringComparison.Ordinal),
            plan));
        Assert.False(FilterReadback.MatchesBlurPlan(
            $"{readback},@censor_blur_999:lavfi=[hflip]",
            plan));
        Assert.False(FilterReadback.MatchesBlurPlan($"{readback},{expected}", plan));
    }

    [Fact]
    public void FindsAllReservedBlurLabelsForCleanup()
    {
        Assert.Equal(
            ["@censor_blur_000", "@censor_blur_old"],
            FilterReadback.FindOwnedBlurLabels(
                "@user:lavfi=[hflip],@censor_blur_000:lavfi=[gblur],@censor_blur_old:lavfi=[vflip]"));
    }

    [Fact]
    public void SingleFilterReadbackRejectsOldOrDuplicatedGraph()
    {
        var expected = AudioCompressionPresets.All[1].Filter!;
        var old = AudioCompressionPresets.All[2].Filter!;
        var expectedReadback = CanonicalReadback(expected);

        Assert.True(FilterReadback.MatchesSingle(
            $"@user:lavfi=graph=anull,{expectedReadback}",
            AudioCompressionPresets.FilterLabel,
            expected));
        Assert.False(FilterReadback.MatchesSingle(
            CanonicalReadback(old),
            AudioCompressionPresets.FilterLabel,
            expected));
        Assert.False(FilterReadback.MatchesSingle(
            $"{expectedReadback},{expectedReadback}",
            AudioCompressionPresets.FilterLabel,
            expected));
    }

    private static string CanonicalReadback(string filter)
    {
        const string marker = ":lavfi=[";
        var markerIndex = filter.IndexOf(marker, StringComparison.Ordinal);
        var graph = filter[(markerIndex + marker.Length)..^1];
        return $"{filter[..markerIndex]}:lavfi=graph=%{Encoding.UTF8.GetByteCount(graph)}%{graph}";
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
