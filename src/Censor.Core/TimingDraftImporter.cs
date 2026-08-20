using System.Globalization;

namespace Censor.Core;

public readonly record struct TimingImportResult(
    int AddedCount,
    int DuplicateCount,
    int RejectedCount);

public static class TimingDraftImporter
{
    private const int MaxImportedDescriptionLength = 512;
    private const int MaxAttributionSourceLength = 128;

    public static TimingImportResult Import(
        ScheduleDraft draft,
        IEnumerable<TimingCandidate> candidates,
        string source,
        long mediaDurationMs,
        int maxIntervals = ScheduleText.MaxIntervals)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaDurationMs);
        if (maxIntervals is < 1 or > ScheduleText.MaxIntervals)
            throw new ArgumentOutOfRangeException(nameof(maxIntervals));
        if (!IsSafeAttributionSource(source))
            throw new ArgumentException("Некорректный источник таймингов.");

        var normalizedSource = ScheduleDraft.NormalizeSingleLine(source)!;
        var existing = draft.Document.Intervals.ToHashSet();
        var additions = new List<CensorInterval>();
        var duplicateCount = 0;
        var rejectedCount = 0;
        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                !IsSafeSourceEntryId(candidate.SourceEntryId) ||
                candidate.Kind != TimingCandidateKind.Interval ||
                candidate.StartMs is not { } startMs ||
                candidate.EndMs is not { } endMs ||
                startMs < 0 ||
                endMs <= startMs ||
                endMs > mediaDurationMs)
            {
                rejectedCount++;
                continue;
            }

            var description = ScheduleDraft.NormalizeSingleLine(
                string.IsNullOrWhiteSpace(candidate.Note)
                ? candidate.RawText
                : candidate.Note) ?? "";
            description = TimingTextParser.RemoveBidiControls(description);
            if (description.Length > MaxImportedDescriptionLength)
            {
                var length = MaxImportedDescriptionLength;
                if (char.IsHighSurrogate(description[length - 1]) &&
                    char.IsLowSurrogate(description[length]))
                {
                    length--;
                }
                description = description[..length].TrimEnd() + "…";
            }
            var fragmentSuffix = candidate.FragmentIndex == 0
                ? ""
                : $".{(long)candidate.FragmentIndex + 1}";
            var interval = new CensorInterval(
                startMs,
                endMs,
                $"[{normalizedSource} #{candidate.SourceEntryId}{fragmentSuffix}] {description}".TrimEnd());
            if (!existing.Add(interval))
            {
                duplicateCount++;
                continue;
            }
            additions.Add(interval);
        }

        var availableIntervals = Math.Max(0, maxIntervals - draft.Document.Intervals.Count);
        if (additions.Count > availableIntervals)
        {
            throw new ArgumentException(
                $"В черновике недостаточно места. Свободных мест: {availableIntervals}.");
        }
        draft.AddRange(additions);
        return new(additions.Count, duplicateCount, rejectedCount);
    }

    internal static bool IsSafeSourceEntryId(string? value)
    {
        if (!long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var numericId) || numericId <= 0)
        {
            return false;
        }
        return value == numericId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSafeAttributionSource(string value) =>
        value.Length <= MaxAttributionSourceLength &&
        value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is ' ' or '.' or '-' or '_' or ':');
}
