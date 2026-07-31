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
    public void InvalidGlobalOffsetIsReportedOutsideIntervalRows()
    {
        var draft = new ScheduleDraft(
            new(new(OffsetMs: ScheduleText.MaxOffsetMs + 1), [], []));

        var diagnostic = Assert.Single(draft.Validate());

        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private static ScheduleDocument Document(params CensorInterval[] intervals) =>
        new(new(), intervals, []);
}
