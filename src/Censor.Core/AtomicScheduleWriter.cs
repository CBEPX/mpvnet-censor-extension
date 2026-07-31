namespace Censor.Core;

public static class AtomicScheduleWriter
{
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

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new ArgumentException("Путь к расписанию должен включать каталог.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        var text = ScheduleText.Serialize(document);
        if (!ScheduleText.Parse(text, maxTextFileBytes, maxIntervals).IsSuccess)
            throw new InvalidOperationException(
                "Расписание не записано: после сохранения оно не проходит повторный разбор.");
        AtomicFile.WriteUtf8Text(
            path,
            text,
            Path.GetFullPath(path) + ".bak",
            createDirectory: false);
    }
}
