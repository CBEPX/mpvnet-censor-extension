using System.Collections.ObjectModel;
using System.Text;

namespace Censor.Core;

public sealed class ScheduleDraft
{
    public const int MaxHistory = 100;

    private readonly List<ScheduleDocument> _redo = [];
    private readonly List<ScheduleDocument> _undo = [];
    private ScheduleDocument _document;
    private ScheduleDocument _savedDocument;
    private bool _forceDirty;

    public ScheduleDraft(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = Copy(document);
        _savedDocument = _document;
    }

    public ScheduleDocument Document => _document;

    public bool IsDirty => _forceDirty || !ReferenceEquals(_document, _savedDocument);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<ParseDiagnostic> Validate(
        int maxIntervals = ScheduleText.MaxIntervals,
        int maxTextFileBytes = ScheduleText.MaxTextFileBytes,
        bool checkSerializedSize = true) =>
        Validate(_document, maxIntervals, maxTextFileBytes, checkSerializedSize);

    public static IReadOnlyList<ParseDiagnostic> Validate(
        ScheduleDocument document,
        int maxIntervals = ScheduleText.MaxIntervals,
        int maxTextFileBytes = ScheduleText.MaxTextFileBytes,
        bool checkSerializedSize = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maxIntervals is < 1 or > ScheduleText.MaxIntervals)
            throw new ArgumentOutOfRangeException(nameof(maxIntervals));
        if (maxTextFileBytes is < 1 or > ScheduleText.MaxTextFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTextFileBytes));
        var diagnostics = new List<ParseDiagnostic>();
        if (document.Metadata.SchemaVersion != 1)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                0,
                1,
                "Поддерживается только schema 1 формата censor-timeline."));
        }
        if (document.Metadata.MediaDurationMs is <= 0)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                0,
                1,
                "Длительность фильма должна быть положительным целым числом."));
        }
        if (document.Metadata.LeadInMs is < 0 || document.Metadata.LeadOutMs is < 0)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                0,
                1,
                "Запас до и после интервала не может быть отрицательным."));
        }
        if (document.Intervals.Count > maxIntervals)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                0,
                1,
                $"В расписании больше {maxIntervals} интервалов."));
        }
        for (var index = 0; index < document.Intervals.Count; index++)
        {
            var interval = document.Intervals[index];
            if (interval.StartMs < 0)
                diagnostics.Add(Error(index, "Начало не может быть отрицательным."));
            if (interval.StartMs >= interval.EndMs)
                diagnostics.Add(Error(index, "Начало должно быть раньше конца."));
            if (interval.EndMs > ScheduleText.MaxTimestampMs)
                diagnostics.Add(Error(index, "Время не может быть позже 99:59:59.999."));
        }

        if (document.Metadata.OffsetMs is < -ScheduleText.MaxOffsetMs or > ScheduleText.MaxOffsetMs)
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                0,
                1,
                "Смещение выходит за допустимый диапазон."));

        if (checkSerializedSize)
        {
            try
            {
                var serialized = ScheduleText.Serialize(document);
                var serializedBytes = Encoding.UTF8.GetByteCount(serialized);
                if (serializedBytes > maxTextFileBytes)
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        0,
                        1,
                        ScheduleText.SizeLimitMessage(maxTextFileBytes)));
                }
                else if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                {
                    var reparsed = ScheduleText.Parse(
                        serialized,
                        maxTextFileBytes,
                        maxIntervals);
                    var parseError = reparsed.Diagnostics.FirstOrDefault(
                        item => item.Severity == DiagnosticSeverity.Error);
                    if (parseError is not null)
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Error,
                            0,
                            1,
                            $"Изменения нельзя сохранить: {parseError.Message}"));
                    }
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or OverflowException)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    0,
                    1,
                    $"Изменения нельзя сохранить: {exception.Message}"));
            }
        }

        return Array.AsReadOnly(diagnostics.ToArray());
    }

    public void Add(long startMs, long endMs, string? note = null) =>
        Change(document => document with
        {
            Intervals = document.Intervals.Append(new(startMs, endMs, NormalizeSingleLine(note))).ToArray(),
        });

    public void AddRange(IEnumerable<CensorInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var additions = intervals
            .Select(interval => interval with { Note = NormalizeSingleLine(interval.Note) })
            .ToArray();
        if (additions.Length == 0)
            return;
        Change(document => document with
        {
            Intervals = document.Intervals.Concat(additions).ToArray(),
        });
    }

    public void Update(int index, long startMs, long endMs, string? note)
    {
        EnsureIndex(index);
        var updated = new CensorInterval(startMs, endMs, NormalizeSingleLine(note));
        if (_document.Intervals[index] == updated)
            return;
        Change(document =>
        {
            var intervals = document.Intervals.ToArray();
            intervals[index] = updated;
            return document with { Intervals = intervals };
        });
    }

    public void Delete(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var selected = indices.Distinct().OrderDescending().ToArray();
        if (selected.Length == 0)
            return;
        foreach (var index in selected)
            EnsureIndex(index);

        Change(document =>
        {
            var intervals = document.Intervals.ToList();
            foreach (var index in selected)
                intervals.RemoveAt(index);
            return document with { Intervals = intervals };
        });
    }

    public void Duplicate(int index)
    {
        EnsureIndex(index);
        Change(document =>
        {
            var intervals = document.Intervals.ToList();
            intervals.Insert(index + 1, intervals[index]);
            return document with { Intervals = intervals };
        });
    }

    public void Merge(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var selected = indices.Distinct().Order().ToArray();
        if (selected.Length < 2)
            throw new ArgumentException("Выберите хотя бы два интервала.", nameof(indices));
        foreach (var index in selected)
            EnsureIndex(index);
        if (selected[^1] - selected[0] + 1 != selected.Length)
            throw new ArgumentException(
                "Для объединения выберите соседние интервалы.",
                nameof(indices));

        Change(document =>
        {
            var source = selected.Select(index => document.Intervals[index]).ToArray();
            var chronological = source
                .OrderBy(interval => interval.StartMs)
                .ThenBy(interval => interval.EndMs)
                .ToArray();
            var coveredUntil = chronological[0].EndMs;
            foreach (var interval in chronological.Skip(1))
            {
                if (interval.StartMs > coveredUntil)
                {
                    throw new ArgumentException(
                        "Между выбранными интервалами есть разрыв. Измените границы вручную.",
                        nameof(indices));
                }
                coveredUntil = Math.Max(coveredUntil, interval.EndMs);
            }
            var merged = new CensorInterval(
                source.Min(interval => interval.StartMs),
                source.Max(interval => interval.EndMs),
                string.Join(
                    "; ",
                    chronological.Select(interval => interval.Note)
                        .Where(note => !string.IsNullOrWhiteSpace(note))
                        .Distinct(StringComparer.Ordinal)));
            var intervals = document.Intervals.ToList();
            foreach (var index in selected.Reverse())
                intervals.RemoveAt(index);
            intervals.Insert(selected[0], merged with
            {
                Note = string.IsNullOrWhiteSpace(merged.Note) ? null : merged.Note,
            });
            return document with { Intervals = intervals };
        });
    }

    public void Split(int index, long positionMs)
    {
        EnsureIndex(index);
        var interval = _document.Intervals[index];
        if (positionMs <= interval.StartMs || positionMs >= interval.EndMs)
            throw new ArgumentOutOfRangeException(
                nameof(positionMs),
                "Точка разделения должна находиться внутри интервала.");

        Change(document =>
        {
            var intervals = document.Intervals.ToList();
            intervals[index] = interval with { EndMs = positionMs };
            intervals.Insert(index + 1, interval with { StartMs = positionMs });
            return document with { Intervals = intervals };
        });
    }

    public void ShiftBoundary(int index, bool start, long deltaMs)
    {
        EnsureIndex(index);
        var interval = _document.Intervals[index];
        Update(
            index,
            start ? checked(interval.StartMs + deltaMs) : interval.StartMs,
            start ? interval.EndMs : checked(interval.EndMs + deltaMs),
            interval.Note);
    }

    public void ShiftAll(long deltaMs) =>
        Change(document => document with
        {
            Intervals = document.Intervals.Select(interval => new CensorInterval(
                checked(interval.StartMs + deltaMs),
                checked(interval.EndMs + deltaMs),
                interval.Note)).ToArray(),
        });

    public void SetOffset(long offsetMs)
    {
        if (offsetMs is < -ScheduleText.MaxOffsetMs or > ScheduleText.MaxOffsetMs)
            throw new ArgumentOutOfRangeException(nameof(offsetMs));

        Change(document => document with
        {
            Metadata = document.Metadata with { OffsetMs = offsetMs },
        });
    }

    public void RetargetMedia(string? title, long? mediaDurationMs)
    {
        _document = Freeze(_document with
        {
            Metadata = _document.Metadata with
            {
                Title = NormalizeSingleLine(title),
                MediaDurationMs = mediaDurationMs,
            },
        });
        _undo.Clear();
        _redo.Clear();
        _forceDirty = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;

        Push(_redo, _document);
        _document = Pop(_undo);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;

        Push(_undo, _document);
        _document = Pop(_redo);
        return true;
    }

    public void MarkSaved()
    {
        _savedDocument = _document;
        _forceDirty = false;
    }

    public bool MarkSaved(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!DocumentsEqual(_document, document))
            return false;

        MarkSaved();
        return true;
    }

    public bool Matches(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DocumentsEqual(_document, document);
    }

    public void MarkDirty() => _forceDirty = true;

    private void Change(Func<ScheduleDocument, ScheduleDocument> update)
    {
        var changed = Freeze(update(_document));
        Push(_undo, _document);
        _document = changed;
        _redo.Clear();
    }

    private void EnsureIndex(int index)
    {
        if ((uint)index >= (uint)_document.Intervals.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static void Push(List<ScheduleDocument> history, ScheduleDocument document)
    {
        history.Add(document);
        if (history.Count > MaxHistory)
            history.RemoveAt(0);
    }

    private static ScheduleDocument Pop(List<ScheduleDocument> history)
    {
        var index = history.Count - 1;
        var document = history[index];
        history.RemoveAt(index);
        return document;
    }

    private static ScheduleDocument Copy(ScheduleDocument document) =>
        document with
        {
            Metadata = document.Metadata with
            {
                Title = NormalizeSingleLine(document.Metadata.Title),
            },
            Intervals = Array.AsReadOnly(document.Intervals
                .Select(interval => interval with { Note = NormalizeSingleLine(interval.Note) })
                .ToArray()),
            PreservedHeaderLines = Array.AsReadOnly(document.PreservedHeaderLines.ToArray()),
        };

    private static ScheduleDocument Freeze(ScheduleDocument document) =>
        document with
        {
            Intervals = Freeze(document.Intervals),
            PreservedHeaderLines = Freeze(document.PreservedHeaderLines),
        };

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values) =>
        values switch
        {
            ReadOnlyCollection<T> => values,
            T[] array => Array.AsReadOnly(array),
            List<T> list => list.AsReadOnly(),
            _ => Array.AsReadOnly(values.ToArray()),
        };

    private static bool DocumentsEqual(ScheduleDocument left, ScheduleDocument right) =>
        left.Metadata == right.Metadata &&
        left.Intervals.SequenceEqual(right.Intervals) &&
        left.PreservedHeaderLines.SequenceEqual(right.PreservedHeaderLines);

    internal static string? NormalizeSingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

    private static ParseDiagnostic Error(int intervalIndex, string message) =>
        new(DiagnosticSeverity.Error, intervalIndex + 1, 1, message);

}
