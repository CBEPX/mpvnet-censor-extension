namespace Censor.Core;

public sealed record AudioCompressionPresetDefinition(
    string Id,
    string DisplayName,
    string? Filter);

public static class AudioCompressionPresets
{
    public const string FilterLabel = "@censor_audio_compression";
    public const string OffId = "off";
    public const string FilmBalancedId = "film-balanced";
    public const string AnimeDialogueId = "anime-dialogue";
    public const string ActionNightId = "action-night";
    public const string MixedAdaptiveId = "mixed-adaptive";

    private static readonly AudioCompressionPresetDefinition[] Definitions =
    [
        new(OffId, "Выключена", null),
        new(
            FilmBalancedId,
            "Фильмы — сбалансированная",
            FilterLabel + ":lavfi=[acompressor=threshold=-20dB:ratio=2.5:attack=20:release=300:makeup=1.6:knee=4:link=maximum:detection=rms,alimiter=limit=0.95:attack=5:release=80:level=0:latency=1]"),
        new(
            AnimeDialogueId,
            "Аниме — мягкая",
            FilterLabel + ":lavfi=[acompressor=threshold=-16dB:ratio=2:attack=12:release=180:makeup=1.35:knee=5:link=maximum:detection=rms,alimiter=limit=0.95:attack=5:release=60:level=0:latency=1]"),
        new(
            ActionNightId,
            "Ночной режим",
            FilterLabel + ":lavfi=[compand=attacks=0.15:decays=0.8:points=-80/-80|-50/-44|-30/-22|-18/-14|0/-6:soft-knee=6:gain=0:volume=-90:delay=0.2,alimiter=limit=0.95:attack=5:release=100:level=0:latency=1]"),
        new(
            MixedAdaptiveId,
            "Адаптивное выравнивание",
            FilterLabel + ":lavfi=[dynaudnorm=f=250:g=7:p=0.90:m=3:r=0.10:n=1:c=1:s=12:t=0.01,alimiter=limit=0.95:attack=5:release=80:level=0:latency=1]"),
    ];

    public static IReadOnlyList<AudioCompressionPresetDefinition> All => Definitions;

    public static AudioCompressionPresetDefinition? Find(string? id) =>
        Definitions.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool IsValid(string? id) => Find(id) is not null;
}
