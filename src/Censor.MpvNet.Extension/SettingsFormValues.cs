namespace Censor.MpvNet.Extension;

internal sealed record SettingsFormValues(
    bool AutoLoadSidecar,
    bool WatchdogEnabled,
    int WatchdogIntervalMs,
    long LeadInMs,
    long LeadOutMs,
    long MergeGapMs,
    long DurationToleranceMs,
    long EarlyIntervalGuardMs);
