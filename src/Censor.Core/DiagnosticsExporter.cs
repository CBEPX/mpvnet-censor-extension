using System.IO.Compression;
using System.Text;

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
                        AddFile(archive, logPath, $"logs/{Path.GetFileName(logPath)}");
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

    private static void AddFile(ZipArchive archive, string path, string name)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }
}
