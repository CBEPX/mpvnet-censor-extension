namespace Censor.Core;

public static class VideoOutputPresets
{
    public static readonly IReadOnlyList<string> PropertyNames =
    [
        "target-colorspace-hint",
        "target-colorspace-hint-mode",
        "target-trc",
        "target-prim",
    ];

    public static IReadOnlyDictionary<string, string> Resolve(
        string mode,
        IReadOnlyDictionary<string, string> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        foreach (var name in PropertyNames)
        {
            if (!baseline.ContainsKey(name))
                throw new ArgumentException($"Не сохранено исходное значение {name}.", nameof(baseline));
        }

        return VideoOutputModes.Normalize(mode) switch
        {
            VideoOutputModes.AutoId => baseline,
            VideoOutputModes.SdrId => Explicit("bt.709", "gamma2.2"),
            VideoOutputModes.HdrId => Explicit("bt.2020", "pq"),
            _ => throw new InvalidOperationException("Неизвестный нормализованный режим видео."),
        };
    }

    public static bool MatchesTarget(string mode, string? primaries, string? transfer) =>
        VideoOutputModes.Normalize(mode) switch
        {
            VideoOutputModes.AutoId => true,
            VideoOutputModes.SdrId => Matches("bt.709", primaries) &&
                Matches("gamma2.2", transfer),
            VideoOutputModes.HdrId => Matches("bt.2020", primaries) &&
                Matches("pq", transfer),
            _ => throw new InvalidOperationException("Неизвестный нормализованный режим видео."),
        };

    private static Dictionary<string, string> Explicit(
        string primaries,
        string transfer) =>
        new Dictionary<string, string>
        {
            ["target-colorspace-hint"] = "yes",
            ["target-colorspace-hint-mode"] = "target",
            ["target-trc"] = transfer,
            ["target-prim"] = primaries,
        };

    private static bool Matches(string expected, string? actual) =>
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase);
}
