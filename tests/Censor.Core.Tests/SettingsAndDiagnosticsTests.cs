using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Censor.Core;

namespace Censor.Core.Tests;

public sealed class SettingsAndDiagnosticsTests
{
    private static byte[] TestPathHashKey() =>
        Enumerable.Repeat((byte)0x5A, ExtensionLog.PathHashKeySize).ToArray();

    [Fact]
    public void SettingsRoundTripAndInvalidFileFallsBackWithoutOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var settings = new ExtensionSettings
        {
            Blur = BlurSettings.Maximum,
            AudioCompressionPreset = AudioCompressionPresets.ActionNightId,
            Logging = new() { IncludePaths = true, RetentionDays = 7 },
        };

        ExtensionSettingsStore.Save(path, settings);
        var loaded = ExtensionSettingsStore.Load(path);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(BlurSettings.Maximum, loaded.Settings.Blur);
        Assert.Equal(AudioCompressionPresets.ActionNightId, loaded.Settings.AudioCompressionPreset);
        Assert.True(loaded.Settings.Logging.IncludePaths);

        const string InvalidSettings =
            """{"schema":2,"futureRoot":{"enabled":true},"limits":{"futureLimit":7}}""";
        File.WriteAllText(path, InvalidSettings);
        var invalid = ExtensionSettingsStore.Load(path);
        Assert.NotEmpty(invalid.Warnings);
        Assert.Equal(2, invalid.Settings.Schema);
        Assert.Equal(BlurSettings.Balanced, invalid.Settings.Blur);
        Assert.Equal(AudioCompressionPresets.OffId, invalid.Settings.AudioCompressionPreset);
        Assert.Equal(InvalidSettings, File.ReadAllText(path));

        Assert.Throws<ArgumentException>(() =>
            ExtensionSettingsStore.Save(path, invalid.Settings));
        Assert.Equal(InvalidSettings, File.ReadAllText(path));

        var converted = ExtensionSettingsStore.ConvertToCurrentSchema(invalid.Settings);
        ExtensionSettingsStore.SaveAfterRepair(path, converted);
        var repairedJson = File.ReadAllText(path);
        Assert.Contains("\"schema\": 1", repairedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("futureRoot", repairedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("futureLimit", repairedJson, StringComparison.Ordinal);
        Assert.Equal(InvalidSettings, File.ReadAllText(path + ".bak"));
        Assert.Equal(InvalidSettings, File.ReadAllText(path + ".pre-repair"));

        ExtensionSettingsStore.Save(path, converted with { Blur = BlurSettings.Maximum });
        Assert.Equal(InvalidSettings, File.ReadAllText(path + ".pre-repair"));
    }

    [Fact]
    public void AudioPresetDefaultsRoundTripsAndRejectsUnknownId()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, """{"schema":1}""");
        Assert.Equal(
            AudioCompressionPresets.OffId,
            ExtensionSettingsStore.Load(path).Settings.AudioCompressionPreset);

        foreach (var preset in AudioCompressionPresets.All)
        {
            ExtensionSettingsStore.Save(
                path,
                new ExtensionSettings { AudioCompressionPreset = preset.Id });
            Assert.Equal(
                preset.Id,
                ExtensionSettingsStore.Load(path).Settings.AudioCompressionPreset);
        }

        File.WriteAllText(
            path,
            """{"schema":1,"audioCompressionPreset":"Film-Balanced"}""");
        var canonical = ExtensionSettingsStore.Load(path);
        Assert.Empty(canonical.Warnings);
        Assert.Equal(
            AudioCompressionPresets.FilmBalancedId,
            canonical.Settings.AudioCompressionPreset);

        File.WriteAllText(
            path,
            """{"schema":1,"audioCompressionPreset":"future","futureRoot":true}""");
        var invalid = ExtensionSettingsStore.Load(path);
        Assert.Contains(
            invalid.Warnings,
            warning => warning.Contains("audioCompressionPreset", StringComparison.Ordinal));
        Assert.Equal(AudioCompressionPresets.OffId, invalid.Settings.AudioCompressionPreset);

        ExtensionSettingsStore.Save(path, invalid.Settings);
        Assert.Contains("\"futureRoot\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAcceptsPropertyNamesWithDifferentCasing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            path,
            """{"Schema":1,"LeadInMs":321,"Blur":{"Sigma":35,"Steps":2}}""");

        var loaded = ExtensionSettingsStore.Load(path);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(321, loaded.Settings.LeadInMs);
        Assert.Equal(new BlurSettings(35, 2), loaded.Settings.Blur);
        Assert.Null(loaded.Settings.AdditionalProperties);
    }

    [Fact]
    public void AudioPresetCatalogHasStableInvariantGraphs()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal(
                [
                    AudioCompressionPresets.OffId,
                    AudioCompressionPresets.FilmBalancedId,
                    AudioCompressionPresets.AnimeDialogueId,
                    AudioCompressionPresets.ActionNightId,
                    AudioCompressionPresets.MixedAdaptiveId,
                ],
                AudioCompressionPresets.All.Select(item => item.Id));
            Assert.Equal(AudioCompressionPresets.All.Count, AudioCompressionPresets.All.Select(item => item.Id).Distinct().Count());
            Assert.Equal(
                [
                    null,
                    "@censor_audio_compression:lavfi=[acompressor=threshold=-20dB:ratio=2.5:attack=20:release=300:makeup=1.6:knee=4:link=maximum:detection=rms,alimiter=limit=0.95:attack=5:release=80:level=0:latency=1]",
                    "@censor_audio_compression:lavfi=[acompressor=threshold=-16dB:ratio=2:attack=12:release=180:makeup=1.35:knee=5:link=maximum:detection=rms,alimiter=limit=0.95:attack=5:release=60:level=0:latency=1]",
                    "@censor_audio_compression:lavfi=[compand=attacks=0.15:decays=0.8:points=-80/-80|-50/-44|-30/-22|-18/-14|0/-6:soft-knee=6:gain=0:volume=-90:delay=0.2,alimiter=limit=0.95:attack=5:release=100:level=0:latency=1]",
                    "@censor_audio_compression:lavfi=[dynaudnorm=f=250:g=7:p=0.90:m=3:r=0.10:n=1:c=1:s=12:t=0.01,alimiter=limit=0.95:attack=5:release=80:level=0:latency=1]",
                ],
                AudioCompressionPresets.All.Select(item => item.Filter));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
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
    public void SettingsUsesBackupAfterCorruptPrimaryAndReportsAllLoggingErrors()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        ExtensionSettingsStore.Save(
            path,
            new ExtensionSettings { AudioCompressionPreset = AudioCompressionPresets.FilmBalancedId });
        ExtensionSettingsStore.Save(
            path,
            new ExtensionSettings { AudioCompressionPreset = AudioCompressionPresets.ActionNightId });
        File.WriteAllText(path, "{broken");

        var restored = ExtensionSettingsStore.Load(path);
        Assert.Equal(AudioCompressionPresets.FilmBalancedId, restored.Settings.AudioCompressionPreset);
        Assert.Contains(
            restored.Warnings,
            warning => warning.Contains("резервная копия", StringComparison.OrdinalIgnoreCase));

        var warnings = ExtensionSettingsStore.Validate(new ExtensionSettings
        {
            Logging = new() { Level = "verbose", RetentionDays = 0 },
        });
        Assert.Contains(warnings, warning => warning.Contains("retentionDays", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("logging.level", StringComparison.Ordinal));
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
        var resolved = settings.ResolveNormalizationOptions(
            new(LeadInMs: 400, OffsetMs: -500));

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

        DraftRecoveryStore.SaveOrDelete(path, document, isDirty: true);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("mediaPath", json, StringComparison.OrdinalIgnoreCase);
        var restored = DraftRecoveryStore.Load(path);
        Assert.Equal(DraftRecoveryStatus.Loaded, restored.Status);
        Assert.Equal(document.Metadata, restored.Document!.Metadata);
        Assert.Equal(document.Intervals, restored.Document.Intervals);

        DraftRecoveryStore.SaveOrDelete(path, document, isDirty: false);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RecoveryPreservesInvalidDraftAndRejectsNullDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "draft.json");
        var invalid = new ScheduleDocument(new(), [new(-1, 2_000)], []);

        DraftRecoveryStore.Save(path, invalid);
        Assert.Equal(invalid.Intervals, DraftRecoveryStore.Load(path).Document!.Intervals);

        File.WriteAllText(path, """{"schema":1,"document":null}""");
        var before = File.ReadAllBytes(path);
        var unreadable = DraftRecoveryStore.Load(path);
        Assert.Equal(DraftRecoveryStatus.Unreadable, unreadable.Status);
        Assert.NotNull(unreadable.Warning);
        Assert.Equal(before, File.ReadAllBytes(path));

        Assert.Equal(
            DraftRecoveryStatus.NotFound,
            DraftRecoveryStore.Load(Path.Combine(directory.Path, "missing.json")).Status);
    }

    [Fact]
    public void LogRedactsPathsAndFlushesConcurrentEvents()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "private-film.mkv");
        var pathHashKey = TestPathHashKey();
        using (var log = new ExtensionLog(directory.Path, 30))
        {
            Parallel.For(0, 20, index => log.Write(
                "event",
                new(1, index),
                new Dictionary<string, object?>
                {
                    ["path"] = ExtensionLog.ProtectPath(
                        sourcePath,
                        includePath: false,
                        pathHashKey),
                }));
        }

        var text = File.ReadAllText(Directory.GetFiles(directory.Path, "*.log").Single());
        Assert.Equal(20, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(sourcePath, text, StringComparison.Ordinal);
        Assert.Contains("hmac-sha256:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PathProtectionReusesInstallationKeyAndChangesAcrossKeys()
    {
        using var directory = new TemporaryDirectory();
        var keyPath = Path.Combine(directory.Path, "path-hash.key");
        var firstKey = ExtensionLog.LoadOrCreatePathHashKey(keyPath);
        var secondKey = ExtensionLog.LoadOrCreatePathHashKey(keyPath);
        var otherKey = firstKey.ToArray();
        otherKey[0] ^= 0xFF;
        const string PrivatePath = @"C:\Private\film.mkv";

        Assert.Equal(firstKey, secondKey);
        Assert.Equal(
            ExtensionLog.ProtectPath(PrivatePath, includePath: false, pathHashKey: firstKey),
            ExtensionLog.ProtectPath(PrivatePath, includePath: false, pathHashKey: secondKey));
        Assert.NotEqual(
            ExtensionLog.ProtectPath(PrivatePath, includePath: false, pathHashKey: firstKey),
            ExtensionLog.ProtectPath(PrivatePath, includePath: false, pathHashKey: otherKey));
    }

    [Fact]
    public void LogUsesInvariantUtcNameAndDeletesOnlyExpiredMatchingFiles()
    {
        using var directory = new TemporaryDirectory();
        var oldLog = Path.Combine(directory.Path, "censor-extension-20000101.log");
        var freshLog = Path.Combine(directory.Path, "censor-extension-fresh.log");
        var unrelated = Path.Combine(directory.Path, "other.log");
        File.WriteAllText(oldLog, "old");
        File.WriteAllText(freshLog, "fresh");
        File.WriteAllText(unrelated, "other");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-40));

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            using var log = new ExtensionLog(directory.Path, 30);
            log.Write("event", new(1, 1));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(unrelated));
        Assert.DoesNotContain(unrelated, ExtensionLog.FindFiles(directory.Path));
        Assert.Contains(
            Directory.GetFiles(directory.Path, "censor-extension-????????.log"),
            path => Path.GetFileName(path).All(character =>
                character is '-' or '.' || char.IsAsciiDigit(character) || char.IsAsciiLetter(character)));
    }

    [Fact]
    public void LogHonorsConfiguredMinimumLevel()
    {
        using var directory = new TemporaryDirectory();
        using (var log = new ExtensionLog(directory.Path, 30, "error"))
        {
            log.Write("info-event", new(1, 1));
            log.Write(
                "error-event",
                new(1, 2),
                level: ExtensionLogLevel.Error);
        }

        var text = File.ReadAllText(Directory.GetFiles(directory.Path, "*.log").Single());
        Assert.DoesNotContain("info-event", text, StringComparison.Ordinal);
        Assert.Contains("error-event", text, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"error\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogDisablesItselfWhenDirectoryCannotBeCreated()
    {
        using var directory = new TemporaryDirectory();
        var blockedDirectory = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockedDirectory, "not a directory");

        using var log = new ExtensionLog(blockedDirectory, 30);

        Assert.False(log.IsEnabled);
        Assert.Null(Record.Exception(() => log.Write("event", new(1, 1))));
    }

    [Fact]
    public void DiagnosticsIncludesScheduleOnlyWithConsent()
    {
        using var directory = new TemporaryDirectory();
        var snapshot = new DiagnosticsSnapshot(
            "{}",
            "{}",
            """[{"severity":1,"line":1,"column":1,"message":"secret schedule line"}]""",
            "{}",
            "{}",
            "{}",
            "{}",
            [],
            "secret schedule");
        var without = Path.Combine(directory.Path, "without.zip");
        var with = Path.Combine(directory.Path, "with.zip");

        DiagnosticsExporter.Export(
            without,
            snapshot,
            includeSchedule: false,
            pathHashKey: TestPathHashKey());
        DiagnosticsExporter.Export(
            with,
            snapshot,
            includeSchedule: true,
            pathHashKey: TestPathHashKey());
        DiagnosticsExporter.Export(
            with,
            snapshot,
            includeSchedule: true,
            pathHashKey: TestPathHashKey());

        using var withoutArchive = ZipFile.OpenRead(without);
        using var withArchive = ZipFile.OpenRead(with);
        Assert.DoesNotContain(withoutArchive.Entries, entry => entry.FullName == "schedule.censor.txt");
        Assert.Contains(withArchive.Entries, entry => entry.FullName == "schedule.censor.txt");
        Assert.Contains(withArchive.Entries, entry => entry.FullName == "af.json");
        using var withoutReportReader = new StreamReader(
            withoutArchive.GetEntry("parse-report.json")!.Open());
        using var withReportReader = new StreamReader(
            withArchive.GetEntry("parse-report.json")!.Open());
        Assert.DoesNotContain(
            "secret schedule line",
            withoutReportReader.ReadToEnd(),
            StringComparison.Ordinal);
        Assert.Contains(
            "secret schedule line",
            withReportReader.ReadToEnd(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsCanReadALiveLog()
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "censor-extension.log");
        File.WriteAllText(logPath, "{\"event\":\"live-event\"}" + Environment.NewLine);
        using var writer = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        var path = Path.Combine(directory.Path, "diagnostics.zip");
        var snapshot = new DiagnosticsSnapshot(
            "{}", "{}", "{}", "{}", "{}", "{}", "{}", [logPath], null);

        DiagnosticsExporter.Export(
            path,
            snapshot,
            includeSchedule: false,
            pathHashKey: TestPathHashKey());

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, entry => entry.FullName == "logs/censor-extension.log");
    }

    [Fact]
    public void DiagnosticsRedactsPathsAndErrorsFromLogs()
    {
        using var directory = new TemporaryDirectory();
        var privatePath = Path.Combine(directory.Path, "Private", "film.mkv");
        var logPath = Path.Combine(directory.Path, "censor-extension.log");
        var line = JsonSerializer.Serialize(new
        {
            @event = "schedule-load-error",
            fields = new Dictionary<string, object?>
            {
                ["schedulePath"] = privatePath,
                ["error"] = $"Unable to open {privatePath}",
                ["rollbackError"] = "Audio filter rollback failed",
                ["ratio"] = "ratio 3 / 4",
            },
        });
        File.WriteAllLines(logPath, [line, $"invalid raw entry {privatePath}"]);
        var path = Path.Combine(directory.Path, "diagnostics.zip");
        var snapshot = new DiagnosticsSnapshot(
            "{}", "{}", "{}", "{}", "{}", "{}", "{}", [logPath], null);

        DiagnosticsExporter.Export(
            path,
            snapshot,
            includeSchedule: false,
            pathHashKey: TestPathHashKey());

        using var archive = ZipFile.OpenRead(path);
        var entry = Assert.Single(archive.Entries, item =>
            item.FullName == "logs/censor-extension.log");
        using var reader = new StreamReader(entry.Open());
        var exported = reader.ReadToEnd();
        Assert.DoesNotContain(privatePath, exported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hmac-sha256:", exported, StringComparison.Ordinal);
        Assert.Contains("Unable to open", exported, StringComparison.Ordinal);
        Assert.Contains("Audio filter rollback failed", exported, StringComparison.Ordinal);
        Assert.Contains("ratio 3 / 4", exported, StringComparison.Ordinal);
        Assert.Contains("log-entry-redacted", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void RootedPathRedactionPreservesTrailingDiagnostics()
    {
        const string path = @"C:\Private Folder\film.mkv";
        var pathHashKey = TestPathHashKey();
        var protectedPath = ExtensionLog.ProtectPath(
            path,
            includePath: false,
            pathHashKey);

        Assert.Equal(
            $"Unable to open {protectedPath} (code 5)",
            DiagnosticsExporter.RedactRootedPaths(
                $"Unable to open {path} (code 5)",
                pathHashKey));
        Assert.Equal(
            $"at Decoder in {protectedPath}:line 42",
            DiagnosticsExporter.RedactRootedPaths(
                $"at Decoder in {path}:line 42",
                pathHashKey));
        Assert.Equal(
            $"Ошибка: «{protectedPath}»",
            DiagnosticsExporter.RedactRootedPaths($"Ошибка: «{path}»", pathHashKey));
        Assert.Equal(
            "Не удалось открыть https://example.com/private/file.mkv (code 5)",
            DiagnosticsExporter.RedactRootedPaths(
                "Не удалось открыть https://example.com/private/file.mkv (code 5)",
                pathHashKey));
    }

    [Fact]
    public void UiStateRoundTripsAndRejectsTinyWindows()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "UiState.json");
        UiStateStore.Save(path, new(10, 20, 800, 600));
        Assert.Equal(new UiState(10, 20, 800, 600), UiStateStore.Load(path));

        UiStateStore.Save(path, new(30, 40, 900, 700));
        Assert.Equal(new UiState(30, 40, 900, 700), UiStateStore.Load(path));

        File.WriteAllText(path, """{"left":0,"top":0,"width":700,"height":500}""");
        Assert.Null(UiStateStore.Load(path));

        File.WriteAllText(path, """{"left":0,"top":0,"width":10,"height":10}""");
        Assert.Null(UiStateStore.Load(path));
    }
}
