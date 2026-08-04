using System.Text;

namespace Censor.Core;

public static class AtomicScheduleWriter
{
    public static byte[] Write(
        string path,
        ScheduleDocument document,
        int maxIntervals = ScheduleText.MaxIntervals,
        int maxTextFileBytes = ScheduleText.MaxTextFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        var validation = ScheduleDraft.Validate(
            document,
            maxIntervals,
            maxTextFileBytes,
            checkSerializedSize: false);
        if (validation.Any(item => item.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Файл интервалов не записан: исправьте ошибки.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new ArgumentException("Путь к расписанию должен включать каталог.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        string text;
        byte[] bytes;
        try
        {
            text = ScheduleText.Serialize(document);
            bytes = Encoding.UTF8.GetBytes(text);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "Файл интервалов не записан: исправьте ошибки.",
                exception);
        }
        if (bytes.Length > maxTextFileBytes)
            throw new InvalidOperationException(
                $"Файл интервалов не записан: размер превышает {maxTextFileBytes} байт.");
        var reparsed = ScheduleText.Parse(text, maxTextFileBytes, maxIntervals);
        if (!reparsed.IsSuccess)
        {
            var parseError = reparsed.Diagnostics.First(item =>
                item.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException(
                "Расписание не записано: после сохранения оно не проходит повторный разбор. " +
                parseError.Message);
        }
        AtomicFile.Write(
            path,
            bytes,
            Path.GetFullPath(path) + ".bak",
            createDirectory: false);
        return bytes;
    }
}
