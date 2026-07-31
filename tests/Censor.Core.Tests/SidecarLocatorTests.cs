using Censor.Core;

namespace Censor.Core.Tests;

public sealed class SidecarLocatorTests
{
    [Fact]
    public void ReturnsExistingSidecarsInTxtSrtVttOrder()
    {
        var directory = Directory.CreateTempSubdirectory("censor-sidecar-tests-");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "Film.mkv");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.censor.vtt"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.censor.txt"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "Film.srt"), "");

            var result = SidecarLocator.Find(mediaPath);

            Assert.Equal(
                [
                    Path.Combine(directory.FullName, "Film.censor.txt"),
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
