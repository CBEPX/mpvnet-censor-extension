using System.Text.Json;
using System.Text.Json.Serialization;

namespace Censor.Core;

public sealed record ExtensionSettings
{
    public int Schema { get; init; } = 1;
    public bool AutoLoadSidecar { get; init; } = true;
    public bool RememberLastScheduleDirectory { get; init; } = true;
    public long LeadInMs { get; init; } = 150;
    public long LeadOutMs { get; init; } = 250;
    public long MergeGapMs { get; init; } = 50;
    public long DurationToleranceMs { get; init; } = 2_000;
    public long EarlyIntervalGuardMs { get; init; } = 3_000;
    public bool WatchdogEnabled { get; init; } = true;
    public int WatchdogIntervalMs { get; init; } = 1_000;
    public BlurSettings Blur { get; init; } = BlurSettings.Balanced;
    public string AudioCompressionPreset { get; init; } = AudioCompressionPresets.OffId;
    public SettingsLimits Limits { get; init; } = new();
    public LoggingSettings Logging { get; init; } = new();
    public string? LastScheduleDirectory { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public NormalizationOptions ResolveNormalizationOptions(ScheduleMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new(
            metadata.LeadInMs ?? LeadInMs,
            metadata.LeadOutMs ?? LeadOutMs,
            metadata.OffsetMs ?? 0,
            MergeGapMs);
    }
}

public sealed record SettingsLimits
{
    public int MaxIntervals { get; init; } = ScheduleText.MaxIntervals;
    public int MaxTextFileBytes { get; init; } = ScheduleText.MaxTextFileBytes;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record LoggingSettings
{
    public string Level { get; init; } = "info";
    public int RetentionDays { get; init; } = 30;
    public bool IncludePaths { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record SettingsLoadResult(
    ExtensionSettings Settings,
    IReadOnlyList<string> Warnings);

public static class ExtensionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    public static SettingsLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new(new(), []);

        try
        {
            return LoadFile(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            var primaryWarning = $"Не удалось прочитать settings.json: {exception.Message}";
            var backupPath = Path.GetFullPath(path) + ".bak";
            if (!File.Exists(backupPath))
                return new(new(), [primaryWarning]);

            try
            {
                var backup = LoadFile(backupPath);
                return new(
                    backup.Settings,
                    [$"Основной settings.json повреждён. Загружена резервная копия.", .. backup.Warnings]);
            }
            catch (Exception backupException) when (
                backupException is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(
                    new(),
                    [
                        primaryWarning,
                        $"Не удалось прочитать резервную копию settings.json: {backupException.Message}",
                    ]);
            }
        }
    }

    public static void Save(string path, ExtensionSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var warnings = Validate(settings);
        if (warnings.Count > 0)
            throw new ArgumentException(string.Join(" ", warnings), nameof(settings));

        AtomicFile.Write(
            path,
            JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions),
            Path.GetFullPath(path) + ".bak");
    }

    public static IReadOnlyList<string> Validate(ExtensionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var warnings = new List<string>();
        if (settings.Schema != 1)
            warnings.Add("Поддерживается только schema 1 настроек.");
        ValidateMilliseconds(settings.LeadInMs, nameof(settings.LeadInMs), warnings);
        ValidateMilliseconds(settings.LeadOutMs, nameof(settings.LeadOutMs), warnings);
        ValidateMilliseconds(settings.MergeGapMs, nameof(settings.MergeGapMs), warnings);
        ValidateMilliseconds(settings.DurationToleranceMs, nameof(settings.DurationToleranceMs), warnings);
        ValidateMilliseconds(settings.EarlyIntervalGuardMs, nameof(settings.EarlyIntervalGuardMs), warnings);
        if (settings.WatchdogIntervalMs is < 250 or > 60_000)
            warnings.Add("Значение watchdogIntervalMs должно быть от 250 до 60000.");
        if (!AudioCompressionPresets.IsValid(settings.AudioCompressionPreset))
            warnings.Add("Неизвестный пресет audioCompressionPreset.");
        if (settings.Blur is null)
        {
            warnings.Add("Поле blur должно быть объектом.");
        }
        else
        {
            if (!double.IsFinite(settings.Blur.Sigma) || settings.Blur.Sigma is < 0.01 or > 1_024)
                warnings.Add("Значение blur.sigma должно быть от 0.01 до 1024.");
            if (settings.Blur.Steps is < 1 or > 6)
                warnings.Add("Значение blur.steps должно быть от 1 до 6.");
        }
        if (settings.Limits is null)
        {
            warnings.Add("Поле limits должно быть объектом.");
        }
        else
        {
            if (settings.Limits.MaxIntervals is < 1 or > ScheduleText.MaxIntervals)
                warnings.Add(
                    $"Значение limits.maxIntervals должно быть от 1 до {ScheduleText.MaxIntervals}.");
            if (settings.Limits.MaxTextFileBytes is < 1 or > ScheduleText.MaxTextFileBytes)
                warnings.Add(
                    $"Значение limits.maxTextFileBytes должно быть от 1 до {ScheduleText.MaxTextFileBytes}.");
        }
        if (settings.Logging is null)
        {
            warnings.Add("Поле logging должно быть объектом.");
        }
        else
        {
            if (settings.Logging.RetentionDays is < 1 or > 365)
                warnings.Add("Значение logging.retentionDays должно быть от 1 до 365.");
            if (!IsLogLevelValid(settings.Logging.Level))
                warnings.Add("Допустимые значения logging.level: debug, info, warning, error.");
        }

        return warnings;
    }

    private static void ValidateMilliseconds(long value, string name, List<string> warnings)
    {
        if (value is < 0 or > 86_400_000)
            warnings.Add($"Значение {name} должно быть от 0 до 86400000.");
    }

    private static ExtensionSettings Normalize(ExtensionSettings settings)
    {
        var defaults = new ExtensionSettings();
        var defaultLimits = new SettingsLimits();
        var defaultLogging = new LoggingSettings();
        var limits = settings.Limits;
        var logging = settings.Logging;
        return settings with
        {
            Schema = 1,
            LeadInMs = ValidMilliseconds(settings.LeadInMs) ? settings.LeadInMs : defaults.LeadInMs,
            LeadOutMs = ValidMilliseconds(settings.LeadOutMs) ? settings.LeadOutMs : defaults.LeadOutMs,
            MergeGapMs = ValidMilliseconds(settings.MergeGapMs) ? settings.MergeGapMs : defaults.MergeGapMs,
            DurationToleranceMs = ValidMilliseconds(settings.DurationToleranceMs)
                ? settings.DurationToleranceMs
                : defaults.DurationToleranceMs,
            EarlyIntervalGuardMs = ValidMilliseconds(settings.EarlyIntervalGuardMs)
                ? settings.EarlyIntervalGuardMs
                : defaults.EarlyIntervalGuardMs,
            WatchdogIntervalMs = settings.WatchdogIntervalMs is >= 250 and <= 60_000
                ? settings.WatchdogIntervalMs
                : defaults.WatchdogIntervalMs,
            AudioCompressionPreset = AudioCompressionPresets.IsValid(settings.AudioCompressionPreset)
                ? settings.AudioCompressionPreset
                : defaults.AudioCompressionPreset,
            Blur = settings.Blur is not null &&
                double.IsFinite(settings.Blur.Sigma) &&
                settings.Blur.Sigma is >= 0.01 and <= 1_024 &&
                settings.Blur.Steps is >= 1 and <= 6
                    ? settings.Blur
                    : defaults.Blur,
            Limits = limits is null
                ? defaultLimits
                : limits with
                {
                    MaxIntervals = limits.MaxIntervals is >= 1 and <= ScheduleText.MaxIntervals
                        ? limits.MaxIntervals
                        : defaultLimits.MaxIntervals,
                    MaxTextFileBytes = limits.MaxTextFileBytes is >= 1 and <= ScheduleText.MaxTextFileBytes
                        ? limits.MaxTextFileBytes
                        : defaultLimits.MaxTextFileBytes,
                },
            Logging = logging is null
                ? defaultLogging
                : logging with
                {
                    Level = IsLogLevelValid(logging.Level) ? logging.Level : defaultLogging.Level,
                    RetentionDays = logging.RetentionDays is >= 1 and <= 365
                        ? logging.RetentionDays
                        : defaultLogging.RetentionDays,
                },
        };
    }

    private static SettingsLoadResult LoadFile(string path)
    {
        var settings = JsonSerializer.Deserialize<ExtensionSettings>(
            File.ReadAllText(path),
            JsonOptions) ?? throw new JsonException("Файл настроек пуст.");
        var warnings = Validate(settings);
        return warnings.Count == 0
            ? new(settings, [])
            : new(Normalize(settings), warnings);
    }

    private static bool ValidMilliseconds(long value) =>
        value is >= 0 and <= 86_400_000;

    private static bool IsLogLevelValid(string? level) =>
        !string.IsNullOrWhiteSpace(level) &&
        (level.Equals("info", StringComparison.OrdinalIgnoreCase) ||
         level.Equals("debug", StringComparison.OrdinalIgnoreCase) ||
         level.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
         level.Equals("error", StringComparison.OrdinalIgnoreCase));
}
