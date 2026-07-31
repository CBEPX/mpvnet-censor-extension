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
                    value.TryGetValue<string>(out var text) &&
                    ShouldRedact(property.Key, text))
                {
                    jsonObject[property.Key] = ExtensionLog.ProtectPath(text, includePath: false);
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
                    value.TryGetValue<string>(out var text) &&
                    LooksLikeRootedPath(text))
                {
                    jsonArray[index] = ExtensionLog.ProtectPath(text, includePath: false);
                }
                else if (jsonArray[index] is { } child)
                {
                    SanitizeNode(child);
                }
            }
        }
    }

    private static bool ShouldRedact(string propertyName, string value) =>
        !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        (propertyName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
         propertyName.Contains("error", StringComparison.OrdinalIgnoreCase) ||
         LooksLikeRootedPath(value));

    private static bool LooksLikeRootedPath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal) ||
        (value.Length >= 3 &&
         char.IsAsciiLetter(value[0]) &&
         value[1] == ':' &&
         value[2] is '\\' or '/');
}
