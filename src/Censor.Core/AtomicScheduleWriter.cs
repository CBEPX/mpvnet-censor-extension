using System.Text;

namespace Censor.Core;

public static class AtomicScheduleWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Write(string path, ScheduleDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Schedule path must include a directory.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = fullPath + ".bak";

        try
        {
            var text = ScheduleText.Serialize(document);
            if (!ScheduleText.Parse(text).IsSuccess)
                throw new InvalidOperationException("Refusing to write a schedule that cannot be parsed back.");

            var bytes = Utf8WithoutBom.GetBytes(text);
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                File.Replace(tempPath, fullPath, backupPath);
            else
                File.Move(tempPath, fullPath);
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
}
