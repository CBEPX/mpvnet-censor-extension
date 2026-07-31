using System.IO.Compression;
using Censor.Core;

namespace Censor.Core.Tests;

public sealed class SettingsAndDiagnosticsTests
{
    [Fact]
    public void SettingsRoundTripAndInvalidFileFallsBackWithoutOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var settings = new ExtensionSettings
        {
            Blur = BlurSettings.Maximum,
            Logging = new() { IncludePaths = true, RetentionDays = 7 },
        };

        ExtensionSettingsStore.Save(path, settings);
        var loaded = ExtensionSettingsStore.Load(path);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(BlurSettings.Maximum, loaded.Settings.Blur);
        Assert.True(loaded.Settings.Logging.IncludePaths);

        const string InvalidSettings =
            """{"schema":2,"futureRoot":{"enabled":true},"limits":{"futureLimit":7}}""";
        File.WriteAllText(path, InvalidSettings);
        var invalid = ExtensionSettingsStore.Load(path);
        Assert.NotEmpty(invalid.Warnings);
        Assert.Equal(BlurSettings.Balanced, invalid.Settings.Blur);
        Assert.Equal(InvalidSettings, File.ReadAllText(path));

        var fallbackPath = Path.Combine(directory.Path, "fallback.json");
        ExtensionSettingsStore.Save(fallbackPath, invalid.Settings);
        var fallbackJson = File.ReadAllText(fallbackPath);
        Assert.Contains("\"futureRoot\"", fallbackJson, StringComparison.Ordinal);
        Assert.Contains("\"futureLimit\"", fallbackJson, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidNestedSettingsAreNormalizedIndependently()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            path,
            """
            {
              "schema": 1,
              "watchdogIntervalMs": 1,
              "blur": { "sigma": 50, "steps": 3 },
              "logging": { "level": null, "retentionDays": 30 }
            }
            """);

        var loaded = ExtensionSettingsStore.Load(path);

        Assert.NotEmpty(loaded.Warnings);
        Assert.Equal(BlurSettings.Maximum, loaded.Settings.Blur);
        Assert.Equal(1_000, loaded.Settings.WatchdogIntervalMs);
        Assert.Equal("info", loaded.Settings.Logging.Level);

        File.WriteAllText(
            path,
            """{"schema":1,"blur":null,"limits":null,"logging":null}""");
        loaded = ExtensionSettingsStore.Load(path);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Equal(BlurSettings.Balanced, loaded.Settings.Blur);
        Assert.NotNull(loaded.Settings.Limits);
        Assert.NotNull(loaded.Settings.Logging);
    }

    [Fact]
    public void ScheduleMetadataOverridesGlobalTimingSettings()
    {
        var settings = new ExtensionSettings
        {
            LeadInMs = 100,
            LeadOutMs = 200,
            MergeGapMs = 300,
        };
        var resolved = ScheduleOptionsResolver.Resolve(
            new(LeadInMs: 400, OffsetMs: -500),
            settings);

        Assert.Equal(new(400, 200, -500, 300), resolved);
    }

    [Fact]
    public void RecoveryContainsNoMediaPathAndIsDeletedAfterResolution()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "draft.json");
        var document = new ScheduleDocument(
            new(Title: "Draft"),
            [new(1_000, 2_000, "note")],
            []);

        DraftRecoveryStore.Save(path, document);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("mediaPath", json, StringComparison.OrdinalIgnoreCase);
        var restored = DraftRecoveryStore.Load(path);
        Assert.NotNull(restored);
        Assert.Equal(document.Metadata, restored.Metadata);
        Assert.Equal(document.Intervals, restored.Intervals);

        DraftRecoveryStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RecoveryPreservesInvalidDraftAndRejectsNullDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "draft.json");
        var invalid = new ScheduleDocument(new(), [new(-1, 2_000)], []);

        DraftRecoveryStore.Save(path, invalid);
        Assert.Equal(invalid.Intervals, DraftRecoveryStore.Load(path)!.Intervals);

        File.WriteAllText(path, """{"schema":1,"document":null}""");
        Assert.Null(DraftRecoveryStore.Load(path));
    }

    [Fact]
    public void LogRedactsPathsAndFlushesConcurrentEvents()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "private-film.mkv");
        using (var log = new ExtensionLog(directory.Path, 30))
        {
            Parallel.For(0, 20, index => log.Write(
                "event",
                new(1, index),
                new Dictionary<string, object?>
                {
                    ["path"] = ExtensionLog.ProtectPath(sourcePath, includePath: false),
                }));
        }

        var text = File.ReadAllText(Directory.GetFiles(directory.Path, "*.log").Single());
        Assert.Equal(20, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(sourcePath, text, StringComparison.Ordinal);
        Assert.Contains("sha256:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsIncludesScheduleOnlyWithConsent()
    {
        using var directory = new TemporaryDirectory();
        var snapshot = new DiagnosticsSnapshot(
            "{}", "{}", "{}", "{}", "{}", "{}", [], "secret schedule");
        var without = Path.Combine(directory.Path, "without.zip");
        var with = Path.Combine(directory.Path, "with.zip");

        DiagnosticsExporter.Export(without, snapshot, includeSchedule: false);
        DiagnosticsExporter.Export(with, snapshot, includeSchedule: true);
        DiagnosticsExporter.Export(with, snapshot, includeSchedule: true);

        using var withoutArchive = ZipFile.OpenRead(without);
        using var withArchive = ZipFile.OpenRead(with);
        Assert.DoesNotContain(withoutArchive.Entries, entry => entry.FullName == "schedule.censor.txt");
        Assert.Contains(withArchive.Entries, entry => entry.FullName == "schedule.censor.txt");
    }

    [Fact]
    public void DiagnosticsCanReadALiveLog()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "censor-extension.log");
        File.WriteAllText(logPath, "live event");
        using var writer = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        var path = Path.Combine(directory.Path, "diagnostics.zip");
        var snapshot = new DiagnosticsSnapshot(
            "{}", "{}", "{}", "{}", "{}", "{}", [logPath], null);

        DiagnosticsExporter.Export(path, snapshot, includeSchedule: false);

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, entry => entry.FullName == "logs/censor-extension.log");
    }

    [Fact]
    public void UiStateRoundTripsAndRejectsTinyWindows()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "UiState.json");
        UiStateStore.Save(path, new(10, 20, 800, 600));
        Assert.Equal(new UiState(10, 20, 800, 600), UiStateStore.Load(path));

        File.WriteAllText(path, """{"left":0,"top":0,"width":10,"height":10}""");
        Assert.Null(UiStateStore.Load(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"censor-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
