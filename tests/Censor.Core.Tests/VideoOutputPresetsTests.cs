using Censor.Core;

namespace Censor.Core.Tests;

public sealed class VideoOutputPresetsTests
{
    private static readonly IReadOnlyDictionary<string, string> Baseline =
        new Dictionary<string, string>
        {
            ["target-colorspace-hint"] = "auto",
            ["target-colorspace-hint-mode"] = "source",
            ["target-trc"] = "auto",
            ["target-prim"] = "auto",
        };

    [Fact]
    public void AutoRestoresCapturedBaselineAndExplicitModesUseExactTargets()
    {
        Assert.Equal(Baseline, VideoOutputPresets.Resolve(VideoOutputModes.AutoId, Baseline));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["target-colorspace-hint"] = "yes",
                ["target-colorspace-hint-mode"] = "target",
                ["target-trc"] = "gamma2.2",
                ["target-prim"] = "bt.709",
            },
            VideoOutputPresets.Resolve(VideoOutputModes.SdrId, Baseline));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["target-colorspace-hint"] = "yes",
                ["target-colorspace-hint-mode"] = "target",
                ["target-trc"] = "pq",
                ["target-prim"] = "bt.2020",
            },
            VideoOutputPresets.Resolve(VideoOutputModes.HdrId, Baseline));
    }

    [Theory]
    [InlineData("sdr", "bt.709", "gamma2.2", true)]
    [InlineData("hdr", "bt.2020", "pq", true)]
    [InlineData("hdr", "bt.709", "gamma2.2", false)]
    [InlineData("auto", "unknown", "unknown", true)]
    public void TargetReadbackMatchesOnlyTheSelectedExplicitMode(
        string mode,
        string primaries,
        string transfer,
        bool expected)
    {
        Assert.Equal(expected, VideoOutputPresets.MatchesTarget(mode, primaries, transfer));
    }

    [Fact]
    public void ExplicitModeRejectsAnIncompleteCapturedBaseline()
    {
        var incomplete = Baseline
            .Where(item => item.Key != "target-trc")
            .ToDictionary();

        Assert.Throws<ArgumentException>(() =>
            VideoOutputPresets.Resolve(VideoOutputModes.SdrId, incomplete));
    }
}
