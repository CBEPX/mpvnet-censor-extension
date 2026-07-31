using System.Text;

namespace Censor.Core;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Write(
        string path,
        ReadOnlySpan<byte> content,
        string? backupPath = null,
        bool createDirectory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Путь должен включать каталог.", nameof(path));
        if (createDirectory)
            Directory.CreateDirectory(directory);
        else if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    tempPath,
                    fullPath,
                    backupPath is null ? null : Path.GetFullPath(backupPath));
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original write failure.
            }
            throw;
        }
    }

    public static void WriteUtf8Text(
        string path,
        string text,
        string? backupPath = null,
        bool createDirectory = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(path, Utf8WithoutBom.GetBytes(text), backupPath, createDirectory);
    }
}
