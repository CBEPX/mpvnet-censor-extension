using System.Globalization;
using System.Text.RegularExpressions;

namespace Censor.Core;

public static class OnlineSourceModes
{
    public const int MaxBaseUrlLength = 2_048;
    public const string DirectRteId = "direct-rte";
    public const string AggregatorId = "aggregator";

    public static bool IsValid(string? value) =>
        value is not null &&
        (value.Equals(DirectRteId, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(AggregatorId, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? value) =>
        value?.Equals(AggregatorId, StringComparison.OrdinalIgnoreCase) == true
            ? AggregatorId
            : DirectRteId;

    public static bool TryNormalizeBaseUrl(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > MaxBaseUrlLength)
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length > 0;
    }
}

public static class VideoOutputModes
{
    public const string AutoId = "auto";
    public const string SdrId = "sdr";
    public const string HdrId = "hdr";

    public static bool IsValid(string? value) =>
        value is not null &&
        (value.Equals(AutoId, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(SdrId, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(HdrId, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? value) =>
        value?.ToLowerInvariant() switch
        {
            SdrId => SdrId,
            HdrId => HdrId,
            _ => AutoId,
        };
}

public enum TimingCandidateKind
{
    Interval,
    Point,
    Unparsed,
    CleanClaim,
}

public sealed record TimingCandidate(
    string SourceEntryId,
    int FragmentIndex,
    TimingCandidateKind Kind,
    long? StartMs,
    long? EndMs,
    string RawText,
    string? Note = null,
    string? Status = null,
    int? VoteScore = null);

public static class TimingTextParser
{
    public const int MaxTextLength = 4_096;

    private static readonly Regex TimestampRegex = new(
        @"(?<![\d:])(?:(?<hours>\d{1,2}):)?(?<minutes>\d{1,4}):(?<seconds>[0-5]\d)(?:[\.,](?<fraction>\d{1,3}))?(?![\d:])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex RangeSeparatorRegex = new(
        @"^\s*(?:-->|-|–|—|→)\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex EdgeSeparatorRegex = new(
        @"^\s*(?:-->|-|–|—|→)\s*|\s*(?:-->|-|–|—|→)\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<TimingCandidate> Parse(
        string sourceEntryId,
        string rawText,
        string? status = null,
        int? voteScore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntryId);
        ArgumentNullException.ThrowIfNull(rawText);
        if (rawText.Length > MaxTextLength)
            throw new ArgumentException($"Текст тайминга длиннее {MaxTextLength} символов.", nameof(rawText));

        rawText = RemoveBidiControls(rawText);
        status = status is null ? null : RemoveBidiControls(status);
        var trimmed = rawText.Trim();
        if (trimmed.TrimEnd('.', '!', ';').Equals("чисто", StringComparison.OrdinalIgnoreCase))
            return [Candidate(TimingCandidateKind.CleanClaim, null, null, null)];

        var matches = TimestampRegex.Matches(rawText).Cast<Match>().ToArray();
        if (matches.Length == 0)
            return [Candidate(TimingCandidateKind.Unparsed, null, null, null)];

        var candidates = new List<TimingCandidate>();
        for (var index = 0; index < matches.Length;)
        {
            if (index + 1 < matches.Length)
            {
                var between = rawText[
                    (matches[index].Index + matches[index].Length)..matches[index + 1].Index];
                if (RangeSeparatorRegex.IsMatch(between))
                {
                    var fragment = ExtractFragment(rawText, matches, index, 2);
                    if (!TryParseTimestamp(matches[index], out var startMs) ||
                        !TryParseTimestamp(matches[index + 1], out var endMs) ||
                        endMs <= startMs)
                    {
                        candidates.Add(Candidate(
                            TimingCandidateKind.Unparsed,
                            null,
                            null,
                            fragment.Note,
                            candidates.Count,
                            fragment.RawText));
                        index += 2;
                        continue;
                    }
                    candidates.Add(Candidate(
                        TimingCandidateKind.Interval,
                        startMs,
                        endMs,
                        fragment.Note,
                        candidates.Count,
                        fragment.RawText));
                    index += 2;
                    continue;
                }
            }

            var pointFragment = ExtractFragment(rawText, matches, index, 1);
            if (!TryParseTimestamp(matches[index], out var pointMs))
            {
                candidates.Add(Candidate(
                    TimingCandidateKind.Unparsed,
                    null,
                    null,
                    pointFragment.Note,
                    candidates.Count,
                    pointFragment.RawText));
                index++;
                continue;
            }
            candidates.Add(Candidate(
                TimingCandidateKind.Point,
                pointMs,
                null,
                pointFragment.Note,
                candidates.Count,
                pointFragment.RawText));
            index++;
        }
        return candidates.AsReadOnly();

        TimingCandidate Candidate(
            TimingCandidateKind kind,
            long? startMs,
            long? endMs,
            string? description,
            int fragmentIndex = 0,
            string? fragmentRawText = null) =>
            new(
                sourceEntryId,
                fragmentIndex,
                kind,
                startMs,
                endMs,
                fragmentRawText ?? rawText,
                description,
                status,
                voteScore);
    }

    private static bool TryParseTimestamp(Match match, out long milliseconds)
    {
        milliseconds = 0;
        if (!long.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !long.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var hoursGroup = match.Groups["hours"];
        var hours = 0L;
        if (hoursGroup.Success &&
            (!long.TryParse(hoursGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
             minutes > 59))
        {
            return false;
        }

        var fraction = match.Groups["fraction"].Value;
        var fractionMs = 0;
        if (fraction.Length > 0 &&
            !int.TryParse(
                fraction.PadRight(3, '0'),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out fractionMs))
        {
            return false;
        }
        try
        {
            milliseconds = checked(((hours * 60 + minutes) * 60 + seconds) * 1_000 + fractionMs);
        }
        catch (OverflowException)
        {
            return false;
        }
        return milliseconds <= ScheduleText.MaxTimestampMs;
    }

    private static string? ExtractNote(string rawText)
    {
        var withoutTimes = TimestampRegex.Replace(rawText, " ");
        var withoutSeparators = EdgeSeparatorRegex.Replace(withoutTimes, " ");
        var note = withoutSeparators.Trim(' ', '\t', ';', ',', '.');
        note = TrimEnclosingPair(note);
        return note.Length == 0 ? null : note;
    }

    private static string TrimEnclosingPair(string value)
    {
        var closing = value.Length > 1
            ? value[0] switch
            {
                '(' => ')',
                '[' => ']',
                '{' => '}',
                _ => '\0',
            }
            : '\0';
        if (closing == '\0' || value[^1] != closing)
            return value;

        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == value[0])
                depth++;
            else if (value[index] == closing && --depth == 0)
                return index == value.Length - 1 ? value[1..^1].Trim() : value;
        }
        return value;
    }

    private static (string RawText, string? Note) ExtractFragment(
        string rawText,
        Match[] matches,
        int firstTimestamp,
        int timestampCount)
    {
        var start = 0;
        if (firstTimestamp > 0)
        {
            var previous = matches[firstTimestamp - 1];
            var separator = FindFragmentSeparator(
                rawText,
                previous.Index + previous.Length,
                matches[firstTimestamp].Index);
            start = separator >= 0 ? separator + 1 : matches[firstTimestamp].Index;
        }
        var nextTimestamp = firstTimestamp + timestampCount;
        var end = rawText.Length;
        if (nextTimestamp < matches.Length)
        {
            var last = matches[nextTimestamp - 1];
            var separator = FindFragmentSeparator(
                rawText,
                last.Index + last.Length,
                matches[nextTimestamp].Index);
            end = separator >= 0 ? separator : matches[nextTimestamp].Index;
        }
        var fragment = rawText[start..end].Trim();
        return (fragment, ExtractNote(fragment));
    }

    private static int FindFragmentSeparator(string value, int start, int end)
    {
        for (var index = end - 1; index >= start; index--)
        {
            if (value[index] is ';' or '\r' or '\n')
                return index;
        }
        return -1;
    }

    internal static string RemoveBidiControls(string value) =>
        value.Any(IsBidiControl)
            ? string.Concat(value.Where(character => !IsBidiControl(character)))
            : value;

    private static bool IsBidiControl(char character) =>
        character is '\u061C' or '\u200E' or '\u200F' or
        >= '\u202A' and <= '\u202E' or
        >= '\u2066' and <= '\u2069';
}
