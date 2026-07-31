using System.Text;

namespace Censor.Core;

public static class AtomicScheduleWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Write(
        string path,
        ScheduleDocument document,
        int maxIntervals = ScheduleText.MaxIntervals,
        int maxTextFileBytes = ScheduleText.MaxTextFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        var validation = new ScheduleDraft(document).Validate(maxIntervals, maxTextFileBytes);
        if (validation.Any(item => item.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Расписание не записано: исправьте ошибки черновика.");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Путь к расписанию должен включать каталог.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = fullPath + ".bak";

        try
        {
            var text = ScheduleText.Serialize(document);
            if (!ScheduleText.Parse(text, maxTextFileBytes, maxIntervals).IsSuccess)
                throw new InvalidOperationException(
                    "Расписание не записано: после сохранения оно не проходит повторный разбор.");

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
