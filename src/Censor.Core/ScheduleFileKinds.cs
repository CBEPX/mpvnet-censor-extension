using System.Diagnostics.CodeAnalysis;

namespace Censor.Core;

public static class ScheduleFileKinds
{
    public const string CanonicalSuffix = ".censor.txt";
    public const string SrtSuffix = ".srt";
    public const string WebVttSuffix = ".vtt";

    public static IReadOnlyList<string> SidecarSuffixes { get; } =
        Array.AsReadOnly([CanonicalSuffix, ".censor" + SrtSuffix, ".censor" + WebVttSuffix]);

    // Manual import accepts ordinary subtitle files; auto-discovery remains .censor.* only.
    public static bool IsSupportedPath(string? path) =>
        IsCanonicalPath(path) || TryGetSubtitleFormat(path, out _);

    public static bool IsCanonicalPath(string? path) =>
        path?.EndsWith(CanonicalSuffix, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryGetSubtitleFormat(
        [NotNullWhen(true)] string? path,
        out SubtitleFormat format)
    {
        if (path?.EndsWith(SrtSuffix, StringComparison.OrdinalIgnoreCase) == true)
        {
            format = SubtitleFormat.Srt;
            return true;
        }
        if (path?.EndsWith(WebVttSuffix, StringComparison.OrdinalIgnoreCase) == true)
        {
            format = SubtitleFormat.WebVtt;
            return true;
        }

        format = default;
        return false;
    }
}
