using Censor.Core;

namespace Censor.Core.Tests;

public sealed class ScheduleDraftTests
{
    [Fact]
    public void EditsMergeSplitAndRoundTripWithoutBakingOffset()
    {
        var draft = new ScheduleDraft(Document(
            new(1_000, 2_000, "one"),
            new(3_000, 4_000, "two")));

        draft.Merge([0, 1]);
        Assert.Equal(new CensorInterval(1_000, 4_000, "one; two"), draft.Document.Intervals[0]);

        draft.Split(0, 2_500);
        Assert.Equal(2, draft.Document.Intervals.Count);
        Assert.All(draft.Document.Intervals, interval => Assert.Equal("one; two", interval.Note));

        draft.SetOffset(750);
        var serialized = ScheduleText.Serialize(draft.Document);
        var parsed = ScheduleText.Parse(serialized);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(1_000, parsed.Document!.Intervals[0].StartMs);
        Assert.Equal(750, parsed.Document.Metadata.OffsetMs);
    }

    [Fact]
    public void MergeRejectsNonAdjacentRowsWithoutChangingDraft()
    {
        var draft = new ScheduleDraft(Document(
            new(1_000, 2_000, "one"),
            new(3_000, 4_000, "two"),
            new(5_000, 6_000, "three")));
        var before = draft.Document;

        var error = Assert.Throws<ArgumentException>(() => draft.Merge([0, 2]));

        Assert.Contains("соседние", error.Message, StringComparison.Ordinal);
        Assert.Same(before, draft.Document);
        Assert.False(draft.CanUndo);
    }

    [Fact]
    public void InvalidDraftCanBeCorrectedButCannotBeConsideredValid()
    {
        var draft = new ScheduleDraft(Document(new CensorInterval(1_000, 2_000)));

        draft.Update(0, 3_000, 2_000, null);
        Assert.Contains(draft.Validate(), item => item.Severity == DiagnosticSeverity.Error);

        draft.Update(0, 1_500, 2_500, null);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void HistoryIsBoundedAndNewEditClearsRedo()
    {
        var draft = new ScheduleDraft(Document(new CensorInterval(0, 1_000)));
        for (var index = 0; index < ScheduleDraft.MaxHistory + 5; index++)
            draft.ShiftBoundary(0, start: false, 1);

        var undoCount = 0;
        while (draft.Undo())
            undoCount++;
        Assert.Equal(ScheduleDraft.MaxHistory, undoCount);

        Assert.True(draft.Redo());
        draft.Add(2_000, 3_000);
        Assert.False(draft.CanRedo);
    }

    [Fact]
    public void DirtyStateTracksSaveAndUndoRedo()
    {
        var draft = new ScheduleDraft(Document(new CensorInterval(0, 1_000)));
        Assert.False(draft.IsDirty);

        draft.Add(2_000, 3_000);
        Assert.True(draft.IsDirty);
        Assert.True(draft.Undo());
        Assert.False(draft.IsDirty);
        Assert.True(draft.Redo());
        draft.MarkSaved();
        Assert.False(draft.IsDirty);

        Assert.True(draft.Undo());
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void FailedEditDoesNotCreateUndoHistory()
    {
        var draft = new ScheduleDraft(Document(new CensorInterval(1, 1_000)));

        Assert.Throws<OverflowException>(() => draft.ShiftAll(long.MaxValue));

        Assert.False(draft.CanUndo);
        Assert.Equal(new CensorInterval(1, 1_000), draft.Document.Intervals[0]);
    }

    [Fact]
    public void CompletedSaveDoesNotMarkNewerEditsAsSaved()
    {
        var draft = new ScheduleDraft(Document(new CensorInterval(0, 1_000)));
        draft.Add(2_000, 3_000);
        var savedSnapshot = draft.Document;
        draft.Add(4_000, 5_000);

        Assert.False(draft.MarkSaved(savedSnapshot));
        Assert.True(draft.IsDirty);
        Assert.True(draft.MarkSaved(draft.Document));
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void DocumentSnapshotIsImmutableAndReusedUntilAnEdit()
    {
        CensorInterval[] source = [new(0, 1_000)];
        var draft = new ScheduleDraft(Document(source));
        var firstDocument = draft.Document;
        source[0] = new(10_000, 11_000);

        Assert.Equal(new CensorInterval(0, 1_000), firstDocument.Intervals[0]);
        Assert.Same(firstDocument, draft.Document);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CensorInterval>)firstDocument.Intervals)[0] = new(20_000, 21_000));

        draft.Update(0, 1_200, 1_100, null);

        Assert.NotSame(firstDocument, draft.Document);
        var changedValidation = draft.Validate(checkSerializedSize: false);
        Assert.Contains(changedValidation, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LargeDraftEditSharesUntouchedIntervals()
    {
        var intervals = Enumerable.Range(0, ScheduleText.MaxIntervals)
            .Select(index => new CensorInterval(index * 2L, index * 2L + 1))
            .ToArray();
        var draft = new ScheduleDraft(Document(intervals));
        var before = draft.Document;

        draft.Update(5_000, 20_000, 20_001, "changed");

        var after = draft.Document;
        Assert.Same(before.Intervals[0], after.Intervals[0]);
        Assert.NotSame(before.Intervals[5_000], after.Intervals[5_000]);
        Assert.Same(before.Intervals[^1], after.Intervals[^1]);
    }

    [Fact]
    public void InvalidGlobalOffsetIsReportedOutsideIntervalRows()
    {
        var draft = new ScheduleDraft(
            new(new(OffsetMs: ScheduleText.MaxOffsetMs + 1), [], []));

        var diagnostic = Assert.Single(draft.Validate());

        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void EnforcesConfiguredIntervalAndUtf8SizeLimits()
    {
        var tooMany = new ScheduleDraft(Document(
            Enumerable.Range(0, ScheduleText.MaxIntervals + 1)
                .Select(index => new CensorInterval(index * 2L, index * 2L + 1))
                .ToArray()));
        var customCount = new ScheduleDraft(Document(
            new(0, 1_000),
            new(2_000, 3_000),
            new(4_000, 5_000)));
        var oversized = new ScheduleDraft(Document(
            new CensorInterval(0, 1_000, new string('я', 100))));

        Assert.Contains(
            tooMany.Validate(),
            item => item.Message.Contains("10000", StringComparison.Ordinal));
        Assert.Contains(
            customCount.Validate(maxIntervals: 2),
            item => item.Message.Contains("2 интервалов", StringComparison.Ordinal));
        Assert.Contains(
            oversized.Validate(maxTextFileBytes: 100),
            item => item.Message.Contains("100 байт", StringComparison.Ordinal));
        Assert.DoesNotContain(
            oversized.Validate(maxTextFileBytes: 100, checkSerializedSize: false),
            item => item.Message.Contains("байт", StringComparison.Ordinal));
    }

    [Fact]
    public void WriterRejectsConfiguredLimitsWithoutChangingExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "schedule.censor.txt");
        File.WriteAllText(path, "old");
        var document = Document(new(0, 1_000), new(2_000, 3_000));

        Assert.Throws<InvalidOperationException>(() =>
            AtomicScheduleWriter.Write(path, document, maxIntervals: 1));

        Assert.Equal("old", File.ReadAllText(path));
    }

    private static ScheduleDocument Document(params CensorInterval[] intervals) =>
        new(new(), intervals, []);
}
