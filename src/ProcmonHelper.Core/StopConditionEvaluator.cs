using ProcmonHelper.Contracts;

namespace ProcmonHelper.Core;

public sealed class StopConditionEvaluator
{
    public StopReason Evaluate(StopOptions options, TimeSpan elapsed, long pmlBytes, long freeBytes, bool targetExited, TimeSpan? timeSinceTargetExit)
    {
        if (options.MaximumDuration is { } duration && elapsed >= duration) return StopReason.DurationReached;
        if (options.MaximumPmlBytes is { } limit && pmlBytes >= limit) return StopReason.SizeLimitReached;
        if (freeBytes <= options.MinimumFreeBytes) return StopReason.FreeSpaceReserveReached;
        if (options.StopAfterTargetExit && targetExited && timeSinceTargetExit >= options.TargetExitDelay) return StopReason.TargetExited;
        return StopReason.None;
    }
}
