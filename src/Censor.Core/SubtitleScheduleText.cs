using System.Globalization;
using System.Text;

namespace Censor.Core;

public enum SubtitleFormat
{
    Srt,
    WebVtt,
}

public static class SubtitleScheduleText
{
    public static ParseResult Import(
        string text,
        SubtitleFormat format,
        int maxTextFileBytes = ScheduleText.MaxTextFileBytes,
        int maxIntervals = ScheduleText.MaxIntervals)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxTextFileBytes is < 1 or > ScheduleText.MaxTextFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTextFileBytes));
        if (maxIntervals is < 1 or > ScheduleText.MaxIntervals)
            throw new ArgumentOutOfRangeException(nameof(maxIntervals));
        if (Encoding.UTF8.GetByteCount(text) > maxTextFileBytes)
        {
            return new(null,
            [
                new(
                    DiagnosticSeverity.Error,
                    1,
                    1,
                    $"Файл субтитров превышает ограничение в {maxTextFileBytes} байт."),
            ]);
        }

        var lines = NormalizeLines(text);
        var diagnostics = new List<ParseDiagnostic>();
        var intervals = new List<CensorInterval>();
        var index = 0;

        if (format == SubtitleFormat.WebVtt)
        {
            if (lines.Length == 0 || !lines[0].StartsWith("WEBVTT", StringComparison.Ordinal))
                return Error(1, "В файле нет заголовка WEBVTT.");

            index = 1;
            while (index < lines.Length && lines[index].Length > 0)
                index++;
        }

        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                index++;
            if (index >= lines.Length)
                break;

            var blockStart = index;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                index++;

            var block = lines[blockStart..index];
            if (format == SubtitleFormat.WebVtt && IsNonCueBlock(block[0]))
                continue;

            var timingIndex = Array.FindIndex(block, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    blockStart + 1,
                    1,
                    "В блоке субтитров нет строки с таймкодами."));
                continue;
            }

            if (!TryParseTiming(block[timingIndex], out var startMs, out var endMs))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    blockStart + timingIndex + 1,
                    1,
                    "Некорректные таймкоды блока субтитров."));
                continue;
            }

            if (startMs >= endMs)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    blockStart + timingIndex + 1,
                    1,
                    "Начало блока субтитров должно быть раньше конца."));
                continue;
            }

            if (intervals.Count >= maxIntervals)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    blockStart + 1,
                    1,
                    $"В файле субтитров больше {maxIntervals} интервалов."));
                break;
            }

            var note = string.Join('\n', block.Skip(timingIndex + 1)).Trim();
            intervals.Add(new(startMs, endMs, note.Length == 0 ? null : note));
        }

        return diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? new(null, diagnostics)
            : new(
                new ScheduleDocument(new ScheduleMetadata(), intervals, []),
                diagnostics);
    }

    public static string Export(ScheduleDocument document, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        if (format == SubtitleFormat.WebVtt)
            builder.Append("WEBVTT\n\n");

        for (var index = 0; index < document.Intervals.Count; index++)
        {
            var interval = document.Intervals[index];
            if (interval.StartMs < 0 || interval.StartMs >= interval.EndMs)
                throw new ArgumentException(
                    "Интервалы должны удовлетворять условию 0 <= начало < конец.",
                    nameof(document));

            if (format == SubtitleFormat.Srt)
                builder.Append(index + 1).Append('\n');

            builder.Append(FormatTimestamp(interval.StartMs, format))
                .Append(" --> ")
                .Append(FormatTimestamp(interval.EndMs, format))
                .Append('\n');

            if (!string.IsNullOrWhiteSpace(interval.Note))
            {
                builder.Append(interval.Note.Replace('\r', ' ').Replace('\n', ' ').Trim())
                    .Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string[] NormalizeLines(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool IsNonCueBlock(string firstLine) =>
        firstLine.Equals("STYLE", StringComparison.Ordinal) ||
        firstLine.Equals("REGION", StringComparison.Ordinal) ||
        firstLine.Equals("NOTE", StringComparison.Ordinal) ||
        firstLine.StartsWith("NOTE ", StringComparison.Ordinal);

    private static bool TryParseTiming(
        string line,
        out long startMs,
        out long endMs)
    {
        startMs = 0;
        endMs = 0;
        var separator = line.IndexOf("-->", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var start = line[..separator].Trim();
        var endAndSettings = line[(separator + 3)..].Trim();
        var settings = endAndSettings.IndexOfAny([' ', '\t']);
        var end = settings >= 0 ? endAndSettings[..settings] : endAndSettings;
        return TryParseTimestamp(start, out startMs) &&
            TryParseTimestamp(end, out endMs);
    }

    private static bool TryParseTimestamp(
        string value,
        out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        var hourText = parts.Length == 3 ? parts[0] : "0";
        var minuteText = parts[^2];
        var secondParts = parts[^1].Split(['.', ',']);
        var fractionLength = secondParts.Length == 2 ? secondParts[1].Length : 0;
        if (secondParts.Length != 2 ||
            !int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(secondParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(secondParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var millis) ||
            hours > 99 ||
            minutes > 59 ||
            seconds > 59 ||
            fractionLength is < 1 or > 3)
        {
            return false;
        }

        milliseconds =
            ((long)hours * 3_600_000) +
            ((long)minutes * 60_000) +
            ((long)seconds * 1_000) +
            millis * (fractionLength switch { 1 => 100, 2 => 10, _ => 1 });
        return true;
    }

    private static string FormatTimestamp(long milliseconds, SubtitleFormat format)
    {
        var timestamp = ScheduleText.FormatTimestamp(milliseconds);
        return format == SubtitleFormat.Srt ? timestamp.Replace('.', ',') : timestamp;
    }

    private static ParseResult Error(int line, string message) =>
        new(null, [new(DiagnosticSeverity.Error, line, 1, message)]);
}
