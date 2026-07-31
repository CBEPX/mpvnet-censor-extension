namespace Censor.Core;

public static class SidecarLocator
{
    private static readonly string[] Extensions =
    [
        ".censor.txt",
        ".censor.srt",
        ".censor.vtt",
    ];

    public static IReadOnlyList<string> Find(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var fullPath = Path.GetFullPath(mediaPath);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Media path must include a directory.", nameof(mediaPath));
        var baseName = Path.GetFileNameWithoutExtension(fullPath);

        return Extensions
            .Select(extension => Path.Combine(directory, baseName + extension))
            .Where(File.Exists)
            .ToArray();
    }
}
