namespace Censor.Core;

public static class SidecarLocator
{
    public static string? SuggestCanonicalPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;

        try
        {
            if (Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                    return null;
                mediaPath = uri.LocalPath;
            }

            var fullPath = Path.GetFullPath(mediaPath);
            var directory = Path.GetDirectoryName(fullPath);
            var baseName = Path.GetFileNameWithoutExtension(fullPath);
            return string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName)
                ? null
                : Path.Combine(directory, baseName + ScheduleFileKinds.CanonicalSuffix);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> Find(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var fullPath = Path.GetFullPath(mediaPath);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Путь к фильму должен включать каталог.", nameof(mediaPath));
        var baseName = Path.GetFileNameWithoutExtension(fullPath);

        return ScheduleFileKinds.SidecarSuffixes
            .Select(extension => Path.Combine(directory, baseName + extension))
            .Where(File.Exists)
            .ToArray();
    }
}
