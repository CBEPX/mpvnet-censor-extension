namespace Censor.Core;

public static class SidecarLocator
{
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
