using Censor.Core;

namespace Censor.Core.Tests;

public sealed class SidecarLocatorTests
{
    [Fact]
    public void SuggestsCanonicalSidecarBesideLocalMedia()
    {
        var mediaPath = Path.Combine(
            Path.GetTempPath(),
            "Film.Directors.Cut.2026.mkv");

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "Film.Directors.Cut.2026.censor.txt"),
            SidecarLocator.SuggestCanonicalPath(mediaPath));
    }

    [Theory]
    [InlineData("Episode #3.mkv", "Episode #3.censor.txt")]
    [InlineData("50%20.mkv", "50%20.censor.txt")]
    [InlineData("50%25.mkv", "50%25.censor.txt")]
    public void PreservesPathCharactersThatHaveUriSemantics(
        string mediaName,
        string sidecarName)
    {
        var mediaPath = Path.Combine(Path.GetTempPath(), mediaName);

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), sidecarName),
            SidecarLocator.SuggestCanonicalPath(mediaPath));
    }

    [Fact]
    public void ConvertsExplicitFileUri()
    {
        var mediaPath = Path.Combine(Path.GetTempPath(), "Film Name.mkv");

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "Film Name.censor.txt"),
            SidecarLocator.SuggestCanonicalPath(new Uri(mediaPath).AbsoluteUri));
    }

    [Fact]
    public void PreservesWindowsDriveAndUncPaths()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            @"C:\Movies\Episode #3.censor.txt",
            SidecarLocator.SuggestCanonicalPath(@"C:\Movies\Episode #3.mkv"));
        Assert.Equal(
            @"\\server\share\50%25.censor.txt",
            SidecarLocator.SuggestCanonicalPath(@"\\server\share\50%25.mkv"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.test/Film.mkv")]
    public void DoesNotSuggestSidecarForMissingOrRemoteMedia(string? mediaPath)
    {
        Assert.Null(SidecarLocator.SuggestCanonicalPath(mediaPath));
    }

    [Theory]
    [InlineData("schedule.censor.txt", true)]
    [InlineData("schedule.srt", true)]
    [InlineData("schedule.censor.vtt", true)]
    [InlineData("schedule.txt", false)]
    [InlineData("schedule.censor.json", false)]
    public void SupportedPathsUseOneSharedContract(string path, bool expected)
    {
        Assert.Equal(expected, ScheduleFileKinds.IsSupportedPath(path));
    }

    [Fact]
    public void SharedParserDispatchesTxtSrtAndWebVtt()
    {
        var inputs = new Dictionary<string, string>
        {
            ["film.censor.txt"] =
                "# censor-timeline: 1\n00:00:01.000 --> 00:00:02.000\n",
            ["film.srt"] =
                "1\n00:00:01,000 --> 00:00:02,000\nblur\n",
            ["film.vtt"] =
                "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nblur\n",
        };

        foreach (var (path, text) in inputs)
        {
            var result = ScheduleFileKinds.Parse(path, text, new());

            Assert.True(result.IsSuccess);
            Assert.Equal(
                new CensorInterval(
                    1_000,
                    2_000,
                    path.EndsWith(".txt", StringComparison.Ordinal) ? null : "blur"),
                result.Document!.Intervals.Single());
        }
    }

    [Fact]
    public void ReturnsExistingCensorSidecarsInTxtSrtVttOrder()
    {
        var directory = Directory.CreateTempSubdirectory("censor-sidecar-tests-");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "Film.mkv");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.censor.vtt"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.censor.txt"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.censor.srt"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.srt"), "");

            var result = SidecarLocator.Find(mediaPath);

            Assert.Equal(
                [
                    Path.Combine(directory.FullName, "Film.censor.txt"),
                    Path.Combine(directory.FullName, "Film.censor.srt"),
                    Path.Combine(directory.FullName, "Film.censor.vtt"),
                ],
                result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
