using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Censor.Core;

public sealed record DiagnosticsSnapshot(
    string ManifestJson,
    string SettingsJson,
    string ParseReportJson,
    string MediaJson,
    string FiltersJson,
    string AudioFiltersJson,
    string EnvironmentJson,
    IReadOnlyList<string> LogFiles,
    string? ScheduleText);

public static class DiagnosticsExporter
{
    public static void Export(
        string path,
        DiagnosticsSnapshot snapshot,
        bool includeSchedule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException(
                "Путь к диагностическому архиву должен включать каталог.",
                nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                AddText(archive, "manifest.json", snapshot.ManifestJson);
                AddText(archive, "settings.sanitized.json", snapshot.SettingsJson);
                AddText(archive, "parse-report.json", snapshot.ParseReportJson);
                AddText(archive, "media.sanitized.json", snapshot.MediaJson);
                AddText(archive, "vf.json", snapshot.FiltersJson);
                AddText(archive, "af.json", snapshot.AudioFiltersJson);
                AddText(archive, "environment.json", snapshot.EnvironmentJson);
                foreach (var logPath in snapshot.LogFiles.Where(File.Exists))
                {
                    try
                    {
                        AddSanitizedLog(
                            archive,
                            logPath,
                            $"logs/{Path.GetFileName(logPath)}");
                    }
                    catch (IOException)
                    {
                        // Log rotation must not prevent the rest of the export.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                if (includeSchedule && snapshot.ScheduleText is not null)
                    AddText(archive, "schedule.censor.txt", snapshot.ScheduleText);
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
                // Preserve the original export failure.
            }
            throw;
        }
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddSanitizedLog(ZipArchive archive, string path, string name)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            if (TrySanitizeLogLine(line, out var sanitized))
                writer.WriteLine(sanitized);
            else
                writer.WriteLine("{\"event\":\"log-entry-redacted\",\"reason\":\"invalid-json\"}");
        }
    }

    private static bool TrySanitizeLogLine(string line, out string sanitized)
    {
        sanitized = "";
        try
        {
            if (JsonNode.Parse(line) is not JsonObject root)
                return false;
            SanitizeNode(root);
            sanitized = root.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void SanitizeNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    jsonObject[property.Key] = SanitizeText(property.Key, text);
                }
                else if (property.Value is not null)
                {
                    SanitizeNode(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    jsonArray[index] = RedactRootedPaths(text);
                }
                else if (jsonArray[index] is { } child)
                {
                    SanitizeNode(child);
                }
            }
        }
    }

    private static string SanitizeText(string propertyName, string value)
    {
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return value;
        return propertyName.Contains("path", StringComparison.OrdinalIgnoreCase)
            ? ExtensionLog.ProtectPath(value, includePath: false)
            : RedactRootedPaths(value);
    }

    public static string RedactRootedPaths(string value)
    {
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return value;

        StringBuilder? sanitized = null;
        var copiedUntil = 0;
        while (FindRootedPathStart(value, copiedUntil) is { } start)
        {
            sanitized ??= new(value.Length);
            sanitized.Append(value, copiedUntil, start - copiedUntil);
            var end = FindRootedPathEnd(value, start);
            sanitized.Append(ExtensionLog.ProtectPath(value[start..end], includePath: false));
            copiedUntil = end;
        }

        if (sanitized is null)
            return value;
        sanitized.Append(value, copiedUntil, value.Length - copiedUntil);
        return sanitized.ToString();
    }

    private static int? FindRootedPathStart(string value, int startIndex)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            if (IsRootedPathStart(value, index))
                return index;
        }
        return null;
    }

    private static bool IsRootedPathStart(string value, int index)
    {
        if (index > 0 && !IsPathBoundary(value[index - 1]))
            return false;

        if (value[index] == '/')
            return index + 1 < value.Length && !char.IsWhiteSpace(value[index + 1]);
        if (value[index] == '\\')
            return index + 1 < value.Length && value[index + 1] == '\\';
        return index + 2 < value.Length &&
            char.IsAsciiLetter(value[index]) &&
            value[index + 1] == ':' &&
            value[index + 2] is '\\' or '/';
    }

    private static bool IsPathBoundary(char value) =>
        char.IsWhiteSpace(value) || value is '"' or '\'' or '(' or '[' or '{' or '=' or ':';

    private static int FindRootedPathEnd(string value, int start)
    {
        var quote = start > 0 && value[start - 1] is '"' or '\''
            ? value[start - 1]
            : '\0';
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] is '\r' or '\n' || quote != '\0' && value[index] == quote)
                return index;
            if (quote == '\0' &&
                (value.AsSpan(index).StartsWith(":line ", StringComparison.OrdinalIgnoreCase) ||
                 value.AsSpan(index).StartsWith(" (code ", StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }
        return value.Length;
    }

}
