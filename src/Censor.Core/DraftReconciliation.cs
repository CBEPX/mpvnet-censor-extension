namespace Censor.Core;

public enum DraftReconciliationAction
{
    RefreshWarnings,
    LinkMatchingDraft,
    KeepDirtyDraft,
    ReplaceDraft,
}

public enum SavedDraftRuntimeAction
{
    None,
    ClearPending,
    UpdatePending,
    StageFromActive,
}

public readonly record struct SavedDraftDecision(
    bool MarkWindowSaved,
    bool UpdateRuntime,
    SavedDraftRuntimeAction RuntimeAction);

public static class DraftReconciliation
{
    public static DraftReconciliationAction Decide(
        bool runtimeIntervalsUnchanged,
        bool draftDirty,
        bool draftMatchesDocument)
    {
        if (runtimeIntervalsUnchanged)
            return DraftReconciliationAction.RefreshWarnings;
        if (draftMatchesDocument)
            return DraftReconciliationAction.LinkMatchingDraft;
        return draftDirty
            ? DraftReconciliationAction.KeepDirtyDraft
            : DraftReconciliationAction.ReplaceDraft;
    }

    public static SavedDraftDecision DecideAfterSave(
        bool isCurrent,
        bool sourceMatchesCurrent,
        bool planIsEmpty,
        bool hasPending,
        bool hasActive)
    {
        if (!isCurrent)
            return default;
        if (!sourceMatchesCurrent)
            return new(true, false, SavedDraftRuntimeAction.None);
        var action = planIsEmpty
            ? SavedDraftRuntimeAction.ClearPending
            : hasPending
                ? SavedDraftRuntimeAction.UpdatePending
                : hasActive
                    ? SavedDraftRuntimeAction.StageFromActive
                    : SavedDraftRuntimeAction.None;
        return new(true, true, action);
    }
}
