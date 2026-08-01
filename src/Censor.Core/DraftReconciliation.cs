namespace Censor.Core;

public enum DraftReconciliationAction
{
    RefreshWarnings,
    LinkMatchingDraft,
    KeepDirtyDraft,
    ReplaceDraft,
}

public static class DraftReconciliation
{
    public static DraftReconciliationAction Decide(
        bool runtimeIntervalsUnchanged,
        bool sourceIntervalsUnchanged,
        bool draftDirty,
        bool draftMatchesDocument)
    {
        if (runtimeIntervalsUnchanged && (draftDirty || sourceIntervalsUnchanged))
            return DraftReconciliationAction.RefreshWarnings;
        if (draftMatchesDocument)
            return DraftReconciliationAction.LinkMatchingDraft;
        return draftDirty
            ? DraftReconciliationAction.KeepDirtyDraft
            : DraftReconciliationAction.ReplaceDraft;
    }
}
