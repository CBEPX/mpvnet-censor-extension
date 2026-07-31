namespace Censor.Core;

public static class WatchdogPolicy
{
    public static bool IsIntervalNear(
        long positionMs,
        IReadOnlyList<NormalizedInterval> intervals,
        long earlyIntervalGuardMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(positionMs);
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentOutOfRangeException.ThrowIfNegative(earlyIntervalGuardMs);

        return intervals.Any(interval =>
            interval.EndMs > positionMs &&
            (interval.StartMs <= positionMs ||
             interval.StartMs - positionMs <= earlyIntervalGuardMs));
    }
}
