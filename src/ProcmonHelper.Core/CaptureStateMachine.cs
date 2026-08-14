using ProcmonHelper.Contracts;

namespace ProcmonHelper.Core;

public sealed class CaptureStateMachine
{
    private static readonly Dictionary<CaptureState, HashSet<CaptureState>> Allowed =
        new Dictionary<CaptureState, HashSet<CaptureState>>
        {
            [CaptureState.Idle] = [CaptureState.Validating, CaptureState.Recovering],
            [CaptureState.Validating] = [CaptureState.Preparing, CaptureState.Failed, CaptureState.Idle],
            [CaptureState.Preparing] = [CaptureState.WaitingForElevation, CaptureState.Failed],
            [CaptureState.WaitingForElevation] = [CaptureState.StartingProcmon, CaptureState.Failed],
            [CaptureState.StartingProcmon] = [CaptureState.WaitingForProcmon, CaptureState.Failed],
            [CaptureState.WaitingForProcmon] = [CaptureState.LaunchingTarget, CaptureState.Capturing, CaptureState.StopRequested, CaptureState.Failed],
            [CaptureState.LaunchingTarget] = [CaptureState.Capturing, CaptureState.StopRequested, CaptureState.Failed],
            [CaptureState.Capturing] = [CaptureState.StopRequested, CaptureState.Failed],
            [CaptureState.StopRequested] = [CaptureState.StoppingProcmon, CaptureState.Failed],
            [CaptureState.StoppingProcmon] = [CaptureState.Finalizing, CaptureState.Failed],
            [CaptureState.Finalizing] = [CaptureState.Exporting, CaptureState.Copying, CaptureState.Completed, CaptureState.CompletedWithWarnings, CaptureState.Failed],
            [CaptureState.Exporting] = [CaptureState.Copying, CaptureState.Completed, CaptureState.CompletedWithWarnings, CaptureState.Failed],
            [CaptureState.Copying] = [CaptureState.Completed, CaptureState.CompletedWithWarnings, CaptureState.Failed],
            [CaptureState.Recovering] = [CaptureState.Finalizing, CaptureState.Exporting, CaptureState.Copying, CaptureState.CompletedWithWarnings, CaptureState.Failed],
            [CaptureState.Completed] = [CaptureState.Idle],
            [CaptureState.CompletedWithWarnings] = [CaptureState.Idle],
            [CaptureState.Failed] = [CaptureState.Idle, CaptureState.Recovering]
        };

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public event EventHandler<CaptureState>? StateChanged;

    public bool CanTransitionTo(CaptureState next) => Allowed.TryGetValue(State, out var states) && states.Contains(next);

    public void TransitionTo(CaptureState next)
    {
        if (!CanTransitionTo(next))
            throw new InvalidOperationException($"Invalid capture state transition: {State} -> {next}.");
        State = next;
        StateChanged?.Invoke(this, next);
    }
}
