using Censor.Core;

namespace Censor.Core.Tests;

public sealed class TimingDraftImporterTests
{
    [Fact]
    public void ImportsValidIntervalsAsOneUndoStepAndSkipsExactDuplicates()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [new(10_000, 12_000, "[timings.rte #1] первая")],
            []));
        var candidates = new[]
        {
            Candidate("1", 10_000, 12_000, "первая"),
            Candidate("2", 20_000, 25_000, "вторая"),
            Candidate("3", 110_000, 115_000, "за пределами фильма"),
            new TimingCandidate("4", 0, TimingCandidateKind.Point, 30_000, null, "00:30"),
        };

        var result = TimingDraftImporter.Import(
            draft,
            candidates,
            "timings.rte",
            100_000);

        Assert.Equal(new TimingImportResult(1, 1, 2), result);
        Assert.Equal(2, draft.Document.Intervals.Count);
        Assert.Equal("[timings.rte #2] вторая", draft.Document.Intervals[1].Note);
        Assert.True(draft.Undo());
        Assert.Single(draft.Document.Intervals);
    }

    [Fact]
    public void DetectsDuplicatesAfterNormalizingAnOnlineNote()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [new(10_000, 12_000, "[timings.rte #1] первая строка вторая строка")],
            []));
        var candidate = Candidate("1", 10_000, 12_000, "первая строка\r\nвторая строка");

        var result = TimingDraftImporter.Import(
            draft,
            [candidate],
            "timings.rte",
            100_000);

        Assert.Equal(new TimingImportResult(0, 1, 0), result);
        Assert.Single(draft.Document.Intervals);
    }

    [Fact]
    public void CapsImportedDescriptionsBeforeTheyReachTheDraft()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [],
            []));
        var candidate = Candidate("9", 10_000, 12_000, new string('я', 4_096));

        var result = TimingDraftImporter.Import(
            draft,
            [candidate],
            "timings.rte kp:301",
            100_000);

        Assert.Equal(1, result.AddedCount);
        Assert.True(Assert.Single(draft.Document.Intervals).Note!.Length <= 600);
    }

    [Fact]
    public void AddsTheFragmentNumberForLaterRangesFromOneSourceEntry()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [],
            []));
        var candidate = Candidate("9", 10_000, 12_000, "вторая сцена") with
        {
            FragmentIndex = 1,
        };

        TimingDraftImporter.Import(
            draft,
            [candidate],
            "timings.rte kp:301",
            100_000);

        Assert.Equal(
            "[timings.rte kp:301 #9.2] вторая сцена",
            Assert.Single(draft.Document.Intervals).Note);
    }

    [Fact]
    public void UsesTheFragmentsOwnRawTextWhenItHasNoDescription()
    {
        var draft = new ScheduleDraft(new(new(MediaDurationMs: 100_000), [], []));
        var candidate = Candidate("10", 10_000, 12_000, "ignored") with
        {
            RawText = "00:10-00:12\u202E",
            Note = null,
        };

        TimingDraftImporter.Import(draft, [candidate], "timings.rte", 100_000);

        Assert.Equal(
            "[timings.rte #10] 00:10-00:12",
            Assert.Single(draft.Document.Intervals).Note);
    }

    [Fact]
    public void RejectsAnEntryIdThatCanForgeTheAttributionPrefix()
    {
        var draft = new ScheduleDraft(new(new(MediaDurationMs: 100_000), [], []));

        var result = TimingDraftImporter.Import(
            draft,
            [Candidate("1] fake", 10_000, 12_000, "сцена")],
            "custom kp:301",
            100_000);

        Assert.Equal(new TimingImportResult(0, 0, 1), result);
        Assert.Empty(draft.Document.Intervals);
    }

    [Fact]
    public void RejectsANonCanonicalEntryId()
    {
        var draft = new ScheduleDraft(new(new(MediaDurationMs: 100_000), [], []));

        var result = TimingDraftImporter.Import(
            draft,
            [Candidate("007", 10_000, 12_000, "сцена")],
            "custom kp:301",
            100_000);

        Assert.Equal(new TimingImportResult(0, 0, 1), result);
        Assert.Empty(draft.Document.Intervals);
    }

    [Fact]
    public void AppliesTheDraftLimitAfterRemovingDuplicates()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [new(10_000, 12_000, "[timings.rte #1] первая")],
            []));

        var result = TimingDraftImporter.Import(
            draft,
            [
                Candidate("1", 10_000, 12_000, "первая"),
                Candidate("2", 20_000, 22_000, "вторая"),
            ],
            "timings.rte",
            100_000,
            maxIntervals: 2);

        Assert.Equal(new TimingImportResult(1, 1, 0), result);
        Assert.Equal(2, draft.Document.Intervals.Count);
    }

    [Fact]
    public void RejectsAnImportThatExceedsTheDraftLimitWithoutChangingIt()
    {
        var draft = new ScheduleDraft(new(
            new(MediaDurationMs: 100_000),
            [new(10_000, 12_000, "existing")],
            []));

        var exception = Assert.Throws<ArgumentException>(() => TimingDraftImporter.Import(
            draft,
            [
                Candidate("2", 20_000, 22_000, "вторая"),
                Candidate("3", 30_000, 32_000, "третья"),
            ],
            "timings.rte",
            100_000,
            maxIntervals: 2));

        Assert.Contains("Свободных мест: 1", exception.Message, StringComparison.Ordinal);
        Assert.Single(draft.Document.Intervals);
    }

    [Fact]
    public void RejectsASourceThatCanForgeTheAttributionPrefix()
    {
        var draft = new ScheduleDraft(new(new(MediaDurationMs: 100_000), [], []));

        Assert.Throws<ArgumentException>(() => TimingDraftImporter.Import(
            draft,
            [Candidate("1", 10_000, 12_000, "сцена")],
            "custom] #fake",
            100_000));
        Assert.Empty(draft.Document.Intervals);
    }

    private static TimingCandidate Candidate(
        string id,
        long startMs,
        long endMs,
        string note) =>
        new(
            id,
            0,
            TimingCandidateKind.Interval,
            startMs,
            endMs,
            $"{startMs}-{endMs} {note}",
            note);
}
