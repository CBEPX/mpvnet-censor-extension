using System.Globalization;
using System.Text;

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
    public const int MaxFilters = 50;

    public static FilterPlan Compile(
        IReadOnlyList<NormalizedInterval> intervals,
        BlurSettings blur)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(blur);
        Validate(intervals, blur);

        if (intervals.Count == 0)
            return new([], 0, blur);

        var chunkCount = (intervals.Count + MaxIntervalsPerChunk - 1) / MaxIntervalsPerChunk;
        if (chunkCount > MaxFilters)
            throw new InvalidOperationException(
                $"План не может содержать больше {MaxFilters} фильтров.");

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
