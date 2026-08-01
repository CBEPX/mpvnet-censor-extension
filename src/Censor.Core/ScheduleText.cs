using System.Globalization;
using System.Text;

namespace Censor.Core;

public static class ScheduleText
{
    public const int MaxTextFileBytes = 2 * 1024 * 1024;
    public const int MaxIntervals = 10_000;
    public const long MaxOffsetMs = 86_400_000;
    public const long MaxTimestampMs = 359_999_999;

    private static readonly HashSet<string> KnownMetadataKeys =
    [
        "censor-timeline",
        "title",
        "media-duration-ms",
        "lead-in-ms",
        "lead-out-ms",
        "offset-ms",
    ];

    public static ParseResult Parse(
        string text,
        int maxTextFileBytes = MaxTextFileBytes,
        int maxIntervals = MaxIntervals)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxTextFileBytes is < 1 or > MaxTextFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTextFileBytes));
        if (maxIntervals is < 1 or > MaxIntervals)
            throw new ArgumentOutOfRangeException(nameof(maxIntervals));

        var diagnostics = new List<ParseDiagnostic>();
        // The file-size limit covers the original UTF-8 bytes, including a BOM.
        if (Encoding.UTF8.GetByteCount(text) > maxTextFileBytes)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                1,
                1,
                $"Расписание превышает ограничение в {maxTextFileBytes} байт."));
            return new(null, diagnostics);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var intervals = new List<CensorInterval>();
        var preservedHeaderLines = new List<string>();
        var seenMetadata = new HashSet<string>(StringComparer.Ordinal);

        var schemaVersion = 1;
        string? title = null;
        long? mediaDurationMs = null;
        long? leadInMs = null;
        long? leadOutMs = null;
        long? offsetMs = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var rawLine = lines[index];
            var line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '#')
            {
                var body = line[1..].TrimStart();
                var colon = body.IndexOf(':');
                if (colon <= 0)
                {
                    preservedHeaderLines.Add(rawLine);
                    continue;
                }

                var key = body[..colon].Trim();
                var value = body[(colon + 1)..].Trim();
                if (!KnownMetadataKeys.Contains(key))
                {
                    preservedHeaderLines.Add(rawLine);
                    if (LooksLikeMetadataKey(key))
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Warning,
                            lineNumber,
                            1,
                            $"Неизвестное необязательное поле metadata «{key}» сохранено без изменений."));
                    }
                    continue;
                }

                if (!seenMetadata.Add(key))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        lineNumber,
                        1,
                        $"Поле metadata «{key}» указано несколько раз."));
                    continue;
                }

                switch (key)
                {
                    case "censor-timeline":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out schemaVersion) ||
                            schemaVersion != 1)
                        {
                            diagnostics.Add(new(
                                DiagnosticSeverity.Error,
                                lineNumber,
                                colon + 3,
                                "Поддерживается только schema 1 формата censor-timeline."));
                        }
                        break;
                    case "title":
                        title = value;
                        break;
                    case "media-duration-ms":
                        mediaDurationMs = ParsePositiveLong(value, key, lineNumber, colon + 3, diagnostics);
                        break;
                    case "lead-in-ms":
                        leadInMs = ParseNonNegativeLong(value, key, lineNumber, colon + 3, diagnostics);
                        break;
                    case "lead-out-ms":
                        leadOutMs = ParseNonNegativeLong(value, key, lineNumber, colon + 3, diagnostics);
                        break;
                    case "offset-ms":
                        offsetMs = ParseOffset(value, lineNumber, colon + 3, diagnostics);
                        break;
                }

                continue;
            }

            if (intervals.Count >= maxIntervals)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    lineNumber,
                    1,
                    $"В расписании больше {maxIntervals} интервалов."));
                break;
            }

            ParseInterval(line, lineNumber, intervals, diagnostics);
        }

        if (mediaDurationMs is null && !seenMetadata.Contains("media-duration-ms"))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Warning,
                1,
                1,
                "Поле media-duration-ms не задано: соответствие фильму проверить нельзя."));
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return new(null, diagnostics);

        var metadata = new ScheduleMetadata(
            schemaVersion,
            title,
            mediaDurationMs,
            leadInMs,
            leadOutMs,
            offsetMs);
        return new(
            new ScheduleDocument(metadata, intervals, preservedHeaderLines),
            diagnostics);
    }

    private static bool LooksLikeMetadataKey(string key)
    {
        if (key.Length == 0 || key[0] is < 'a' or > 'z')
            return false;

        foreach (var character in key.AsSpan(1))
        {
            if ((character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '-')
            {
                continue;
            }
            return false;
        }
        return key.Contains('-');
    }

    public static string Serialize(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Metadata.Title?.Contains('\r') == true ||
            document.Metadata.Title?.Contains('\n') == true)
        {
            throw new ArgumentException("Заголовок должен помещаться в одну строку.", nameof(document));
        }

        var builder = new StringBuilder();
        builder.Append("# censor-timeline: ")
            .Append(document.Metadata.SchemaVersion)
            .Append('\n');
        AppendMetadata(builder, "title", document.Metadata.Title);
        AppendMetadata(builder, "media-duration-ms", document.Metadata.MediaDurationMs);
        AppendMetadata(builder, "lead-in-ms", document.Metadata.LeadInMs);
        AppendMetadata(builder, "lead-out-ms", document.Metadata.LeadOutMs);
        AppendMetadata(builder, "offset-ms", document.Metadata.OffsetMs);

        foreach (var line in document.PreservedHeaderLines)
        {
            if (!line.TrimStart().StartsWith('#') || line.Contains('\r') || line.Contains('\n'))
                throw new ArgumentException(
                    "Сохранённые строки заголовка должны быть однострочными комментариями.",
                    nameof(document));
            builder.Append(line).Append('\n');
        }

        if (document.Intervals.Count > 0)
            builder.Append('\n');

        foreach (var interval in document.Intervals)
        {
            if (interval.Note?.Contains('\r') == true ||
                interval.Note?.Contains('\n') == true)
            {
                throw new ArgumentException(
                    "Заметка к интервалу должна помещаться в одну строку.",
                    nameof(document));
            }
            builder.Append(FormatTimestamp(interval.StartMs))
                .Append(" --> ")
                .Append(FormatTimestamp(interval.EndMs));

            if (!string.IsNullOrWhiteSpace(interval.Note))
            {
                builder.Append(" | ")
                    .Append(interval.Note);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static bool TryParseTimestamp(ReadOnlySpan<char> value, out long milliseconds)
    {
        milliseconds = 0;
        if (value.Length != 12 ||
            value[2] != ':' ||
            value[5] != ':' ||
            (value[8] != '.' && value[8] != ',') ||
            !int.TryParse(value[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(value.Slice(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(value.Slice(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(value.Slice(9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var millis) ||
            minutes > 59 ||
            seconds > 59)
        {
            return false;
        }

        milliseconds = checked(
            ((long)hours * 3_600_000) +
            ((long)minutes * 60_000) +
            ((long)seconds * 1_000) +
            millis);
        return true;
    }

    public static bool TryParseDraftTimestamp(
        ReadOnlySpan<char> value,
        out long milliseconds)
    {
        milliseconds = 0;
        var negative = value.Length > 0 && value[0] == '-';
        if (negative)
            value = value[1..];
        var firstColon = value.IndexOf(':');
        if (firstColon < 1 || value.Length - firstColon != 10)
            return false;
        var tail = value[firstColon..];
        if (tail[3] != ':' || tail[6] is not ('.' or ',') ||
            !long.TryParse(value[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(tail.Slice(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(tail.Slice(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(tail.Slice(7, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var millis) ||
            minutes > 59 || seconds > 59)
        {
            return false;
        }

        try
        {
            milliseconds = checked(
                ((hours * 60 + minutes) * 60 + seconds) * 1_000 + millis);
            if (negative)
                milliseconds = checked(-milliseconds);
            return true;
        }
        catch (OverflowException)
        {
            milliseconds = 0;
            return false;
        }
    }

    internal static string FormatTimestamp(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (milliseconds > MaxTimestampMs)
            throw new ArgumentOutOfRangeException(
                nameof(milliseconds),
                "Время не может быть позже 99:59:59.999.");

        var hours = milliseconds / 3_600_000;
        var minutes = (milliseconds / 60_000) % 60;
        var seconds = (milliseconds / 1_000) % 60;
        var millis = milliseconds % 1_000;
        return FormattableString.Invariant($"{hours:D2}:{minutes:D2}:{seconds:D2}.{millis:D3}");
    }

    private static void ParseInterval(
        string line,
        int lineNumber,
        List<CensorInterval> intervals,
        List<ParseDiagnostic> diagnostics)
    {
        var separator = line.IndexOf("-->", StringComparison.Ordinal);
        if (separator < 0)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                lineNumber,
                1,
                "В строке интервала нет разделителя '-->'."));
            return;
        }

        var startText = line[..separator].Trim();
        var remainder = line[(separator + 3)..].Trim();
        var noteSeparator = remainder.IndexOf('|');
        var endText = noteSeparator >= 0 ? remainder[..noteSeparator].Trim() : remainder;
        var noteText = noteSeparator >= 0 ? remainder[(noteSeparator + 1)..].Trim() : null;
        var note = string.IsNullOrWhiteSpace(noteText) ? null : noteText;

        if (!TryParseTimestamp(startText, out var startMs))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                lineNumber,
                1,
                $"Некорректное время начала «{startText}»."));
            return;
        }

        if (!TryParseTimestamp(endText, out var endMs))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                lineNumber,
                separator + 4,
                $"Некорректное время окончания «{endText}»."));
            return;
        }

        if (startMs >= endMs)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                lineNumber,
                1,
                "Начало интервала должно быть раньше конца."));
            return;
        }

        intervals.Add(new CensorInterval(startMs, endMs, note));
    }

    private static long? ParsePositiveLong(
        string value,
        string key,
        int line,
        int column,
        List<ParseDiagnostic> diagnostics)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;

        diagnostics.Add(new(
            DiagnosticSeverity.Error,
            line,
            column,
            $"Поле metadata «{key}» должно быть положительным целым числом."));
        return null;
    }

    private static long? ParseNonNegativeLong(
        string value,
        string key,
        int line,
        int column,
        List<ParseDiagnostic> diagnostics)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0)
            return result;

        diagnostics.Add(new(
            DiagnosticSeverity.Error,
            line,
            column,
            $"Поле metadata «{key}» должно быть неотрицательным целым числом."));
        return null;
    }

    private static long? ParseOffset(
        string value,
        int line,
        int column,
        List<ParseDiagnostic> diagnostics)
    {
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result) &&
            result is >= -MaxOffsetMs and <= MaxOffsetMs)
        {
            return result;
        }

        diagnostics.Add(new(
            DiagnosticSeverity.Error,
            line,
            column,
            $"Значение offset-ms должно быть от {-MaxOffsetMs} до {MaxOffsetMs}."));
        return null;
    }

    private static void AppendMetadata(StringBuilder builder, string key, object? value)
    {
        if (value is not null)
        {
            builder.Append("# ")
                .Append(key)
                .Append(": ")
                .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }
}
