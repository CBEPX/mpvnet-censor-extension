using Censor.Core;

namespace Censor.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void ReplacesFileCreatesBackupAndLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var backupPath = path + ".bak";
        File.WriteAllText(path, "old");

        AtomicFile.WriteUtf8Text(path, "новый", backupPath, createDirectory: false);

        Assert.Equal("новый", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(backupPath));
        Assert.Empty(TemporaryFiles(directory.Path, path));
        if (OperatingSystem.IsWindows())
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void FailedReplacementPreservesOriginalAndDeletesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "old");

        Assert.Throws<ArgumentException>(() => AtomicFile.WriteUtf8Text(
            path,
            "new",
            Path.Combine(directory.Path, "invalid\0backup"),
            createDirectory: false));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(directory.Path, path));
    }

    [Fact]
    public void DeletesOnlyStaleTemporaryFilesForTheSameTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "schedule.censor.txt");
        var stale = Path.Combine(directory.Path, ".schedule.censor.txt.stale.tmp");
        var fresh = Path.Combine(directory.Path, ".schedule.censor.txt.fresh.tmp");
        var unrelated = Path.Combine(directory.Path, ".other.censor.txt.stale.tmp");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.WriteAllText(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));

        AtomicFile.WriteUtf8Text(path, "saved");

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("saved", File.ReadAllText(path));
    }

    private static string[] TemporaryFiles(string directory, string path) =>
        Directory.GetFiles(directory, $".{Path.GetFileName(path)}.*.tmp");
}
