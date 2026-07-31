namespace Censor.Core;

public sealed record NormalizationOptions(
    long LeadInMs = 150,
    long LeadOutMs = 250,
    long OffsetMs = 0,
    long MergeGapMs = 50);

public readonly record struct NormalizedInterval(long StartMs, long EndMs);

public static class ScheduleNormalizer
{
    public static IReadOnlyList<NormalizedInterval> Normalize(
        IReadOnlyList<CensorInterval> intervals,
        NormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var adjusted = new List<NormalizedInterval>(intervals.Count);
        foreach (var interval in intervals)
        {
            if (interval.StartMs < 0 || interval.StartMs >= interval.EndMs)
                throw new ArgumentException("Intervals must satisfy 0 <= start < end.", nameof(intervals));

            var start = checked(interval.StartMs - options.LeadInMs + options.OffsetMs);
            var end = checked(interval.EndMs + options.LeadOutMs + options.OffsetMs);
            if (end <= 0)
                continue;

            adjusted.Add(new(Math.Max(0, start), end));
        }

        adjusted.Sort(static (left, right) =>
        {
            var byStart = left.StartMs.CompareTo(right.StartMs);
            return byStart != 0 ? byStart : left.EndMs.CompareTo(right.EndMs);
        });

        if (adjusted.Count < 2)
            return adjusted;

        var merged = new List<NormalizedInterval>(adjusted.Count);
        var current = adjusted[0];
        foreach (var next in adjusted.Skip(1))
        {
            var overlapsOrIsClose =
                next.StartMs <= current.EndMs ||
                next.StartMs - current.EndMs <= options.MergeGapMs;
            if (overlapsOrIsClose)
            {
                current = current with { EndMs = Math.Max(current.EndMs, next.EndMs) };
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    private static void Validate(NormalizationOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.LeadInMs);
        ArgumentOutOfRangeException.ThrowIfNegative(options.LeadOutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MergeGapMs);

        if (options.OffsetMs is < -ScheduleText.MaxOffsetMs or > ScheduleText.MaxOffsetMs)
            throw new ArgumentOutOfRangeException(nameof(options), "Offset is outside the supported range.");
    }
}
