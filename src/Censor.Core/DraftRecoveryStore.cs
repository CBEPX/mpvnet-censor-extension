using System.Text;
using System.Text.Json;

namespace Censor.Core;

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
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException(
                "Путь к файлу восстановления должен включать каталог.",
                nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original recovery write failure.
            }
            throw;
        }
    }

    public static ScheduleDocument? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return null;

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
                document.Intervals.Any(interval => interval is null))
            {
                return null;
            }
            return new(
                document.Metadata with { },
                document.Intervals.Select(interval => interval with { }).ToArray(),
                document.PreservedHeaderLines.ToArray());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
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
