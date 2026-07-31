namespace Censor.Core;

public static class ScheduleOptionsResolver
{
    public static NormalizationOptions Resolve(
        ScheduleMetadata metadata,
        ExtensionSettings settings) =>
        new(
            metadata.LeadInMs ?? settings.LeadInMs,
            metadata.LeadOutMs ?? settings.LeadOutMs,
            metadata.OffsetMs ?? 0,
            settings.MergeGapMs);
}
