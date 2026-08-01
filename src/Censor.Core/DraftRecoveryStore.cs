using System.Text.Json;

namespace Censor.Core;

public enum DraftRecoveryStatus
{
    NotFound,
    Loaded,
    Unreadable,
}

public sealed record DraftRecoveryLoadResult(
    DraftRecoveryStatus Status,
    ScheduleDocument? Document = null,
    string? Warning = null);

public static class DraftRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Save(string path, ScheduleDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        var envelope = new RecoveryEnvelope(1, document, DateTimeOffset.UtcNow);
        AtomicFile.WriteUtf8Text(path, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public static void SaveOrDelete(
        string path,
        ScheduleDocument document,
        bool isDirty)
    {
        if (isDirty)
            Save(path, document);
        else
            Delete(path);
    }

    public static DraftRecoveryLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new(DraftRecoveryStatus.NotFound);

        try
        {
            var envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(
                File.ReadAllText(path),
                JsonOptions);
            var document = envelope?.Document;
            if (envelope is null ||
                envelope.Schema != 1 ||
                document is null ||
                document.Metadata is null ||
                document.Intervals is null ||
                document.PreservedHeaderLines is null ||
                document.Intervals.Any(interval => interval is null) ||
                document.PreservedHeaderLines.Any(line => line is null))
            {
                return new(
                    DraftRecoveryStatus.Unreadable,
                    Warning: "Файл восстановления повреждён. Он оставлен без изменений.");
            }
            return new(
                DraftRecoveryStatus.Loaded,
                new(
                    document.Metadata with { },
                    document.Intervals.Select(interval => interval with { }).ToArray(),
                    document.PreservedHeaderLines.ToArray()));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(
                DraftRecoveryStatus.Unreadable,
                Warning: $"Не удалось прочитать файл восстановления: {exception.Message}");
        }
    }

    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record RecoveryEnvelope(
        int Schema,
        ScheduleDocument? Document,
        DateTimeOffset SavedAtUtc);
}
