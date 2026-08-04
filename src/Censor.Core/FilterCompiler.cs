using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Censor.Core;

public sealed record BlurSettings(double Sigma = 40, int Steps = 2)
{
    public static BlurSettings Moderate { get; } = new(30, 2);

    public static BlurSettings Balanced { get; } = new();

    public static BlurSettings Maximum { get; } = new(50, 3);
}

public sealed record FilterChunk(string Label, string Filter);

public sealed record FilterPlan(
    IReadOnlyList<FilterChunk> Chunks,
    int IntervalCount,
    BlurSettings Blur);

public static class FilterCompiler
{
    public const int MaxIntervalsPerChunk = 500;

    public static FilterPlan Compile(
        IReadOnlyList<NormalizedInterval> intervals,
        BlurSettings blur)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(blur);
        if (intervals.Count > ScheduleText.MaxIntervals)
            throw new ArgumentOutOfRangeException(nameof(intervals));
        Validate(intervals, blur);

        if (intervals.Count == 0)
            return new([], 0, blur);

        // ScheduleText.MaxIntervals bounds total input; this limit bounds one mpv expression.
        var chunkCount = (intervals.Count + MaxIntervalsPerChunk - 1) / MaxIntervalsPerChunk;
        var sigma = blur.Sigma.ToString("R", CultureInfo.InvariantCulture);
        var chunks = new List<FilterChunk>(chunkCount);
        for (var index = 0; index < intervals.Count; index += MaxIntervalsPerChunk)
        {
            var count = Math.Min(MaxIntervalsPerChunk, intervals.Count - index);
            var expression = BuildExpression(intervals, index, count);
            var label = $"@censor_blur_{chunks.Count:D3}";
            var filter = $"{label}:lavfi=[gblur=sigma={sigma}:steps={blur.Steps}:enable='{expression}']";
            chunks.Add(new(label, filter));
        }

        return new(chunks, intervals.Count, blur);
    }

    private static string BuildExpression(
        IReadOnlyList<NormalizedInterval> intervals,
        int start,
        int count)
    {
        var builder = new StringBuilder(count * 40);
        for (var index = start; index < start + count; index++)
        {
            if (builder.Length > 0)
                builder.Append('+');

            var interval = intervals[index];
            builder.Append("(gte(t,")
                .Append(FormatSeconds(interval.StartMs))
                .Append(")*lt(t,")
                .Append(FormatSeconds(interval.EndMs))
                .Append("))");
        }

        return builder.ToString();
    }

    private static string FormatSeconds(long milliseconds) =>
        FormattableString.Invariant($"{milliseconds / 1_000}.{milliseconds % 1_000:D3}");

    private static void Validate(
        IReadOnlyList<NormalizedInterval> intervals,
        BlurSettings blur)
    {
        if (!double.IsFinite(blur.Sigma) || blur.Sigma is < 0.01 or > 1_024)
            throw new ArgumentOutOfRangeException(
                nameof(blur),
                "Значение sigma должно быть от 0.01 до 1024.");
        if (blur.Steps is < 1 or > 6)
            throw new ArgumentOutOfRangeException(
                nameof(blur),
                "Значение steps должно быть от 1 до 6.");

        for (var index = 0; index < intervals.Count; index++)
        {
            var interval = intervals[index];
            if (interval.StartMs < 0 || interval.StartMs >= interval.EndMs)
                throw new ArgumentException(
                    "Нормализованные интервалы должны удовлетворять условию 0 <= начало < конец.",
                    nameof(intervals));
            if (index > 0 && intervals[index - 1].EndMs >= interval.StartMs)
                throw new ArgumentException(
                    "Нормализованные интервалы должны быть отсортированы и не пересекаться.",
                    nameof(intervals));
        }
    }
}

public static class FilterReadback
{
    private static readonly Regex OwnedLabelPattern = new(
        "@censor_blur_[^:,\\s]+:",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool MatchesBlurPlan(string filters, FilterPlan plan)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(plan);

        var ownedLabels = FindOwnedBlurLabels(filters);
        if (ownedLabels.Count != plan.Chunks.Count)
            return false;

        var expectedLabels = plan.Chunks
            .Select(chunk => chunk.Label)
            .ToHashSet(StringComparer.Ordinal);
        return ownedLabels.Distinct(StringComparer.Ordinal).Count() == ownedLabels.Count &&
            ownedLabels.All(expectedLabels.Contains) &&
            plan.Chunks.All(chunk => filters.Contains(
                CanonicalizeLavfi(chunk.Filter),
                StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> FindOwnedBlurLabels(string filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var labels = new List<string>();
        foreach (Match match in OwnedLabelPattern.Matches(filters))
            labels.Add(match.Value[..^1]);

        return labels;
    }

    public static bool MatchesSingle(string filters, string label, string expectedFilter)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFilter);

        var marker = label + ":";
        var first = filters.IndexOf(marker, StringComparison.Ordinal);
        return first >= 0 &&
            first == filters.LastIndexOf(marker, StringComparison.Ordinal) &&
            filters.Contains(CanonicalizeLavfi(expectedFilter), StringComparison.Ordinal);
    }

    private static string CanonicalizeLavfi(string filter)
    {
        // Mirrors mpv's length-prefixed %N% string form for filter-list properties.
        const string marker = ":lavfi=[";
        var markerIndex = filter.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0 || !filter.EndsWith(']'))
            throw new ArgumentException("Ожидался labeled lavfi filter.", nameof(filter));

        var graph = filter[(markerIndex + marker.Length)..^1];
        return $"{filter[..markerIndex]}:lavfi=graph=%{Encoding.UTF8.GetByteCount(graph)}%{graph}";
    }
}
