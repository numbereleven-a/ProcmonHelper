using ProcmonHelper.Contracts;
using ProcmonHelper.Core;

namespace ProcmonHelper.Core.Tests;

public sealed class StateMachineTests
{
    [Fact]
    public void ValidLifecycle_IsAccepted()
    {
        var state = new CaptureStateMachine();
        foreach (var next in new[] { CaptureState.Validating, CaptureState.Preparing, CaptureState.WaitingForElevation, CaptureState.StartingProcmon, CaptureState.WaitingForProcmon, CaptureState.LaunchingTarget, CaptureState.Capturing, CaptureState.StopRequested, CaptureState.StoppingProcmon, CaptureState.Finalizing, CaptureState.Completed })
            state.TransitionTo(next);
        Assert.Equal(CaptureState.Completed, state.State);
    }

    [Fact]
    public void InvalidTransition_IsRejected() => Assert.Throws<InvalidOperationException>(() => new CaptureStateMachine().TransitionTo(CaptureState.Capturing));

    [Fact]
    public void MonitoringOnlyLifecycleSkipsTargetLaunch()
    {
        var state = new CaptureStateMachine();
        foreach (var next in new[] { CaptureState.Validating, CaptureState.Preparing, CaptureState.WaitingForElevation, CaptureState.StartingProcmon, CaptureState.WaitingForProcmon, CaptureState.Capturing })
            state.TransitionTo(next);
        Assert.Equal(CaptureState.Capturing, state.State);
    }
}

public sealed class StopConditionTests
{
    private readonly StopConditionEvaluator _evaluator = new();
    [Fact] public void DurationWins() => Assert.Equal(StopReason.DurationReached, _evaluator.Evaluate(new() { MaximumDuration = TimeSpan.FromMinutes(1), MinimumFreeBytes = 1 }, TimeSpan.FromMinutes(1), 0, 100, false, null));
    [Fact] public void SegmentTotalTriggersSize() => Assert.Equal(StopReason.SizeLimitReached, _evaluator.Evaluate(new() { MaximumPmlBytes = 100, MinimumFreeBytes = 1 }, TimeSpan.Zero, 100, 1000, false, null));
    [Fact] public void DelayedTargetExitWaits() => Assert.Equal(StopReason.None, _evaluator.Evaluate(new() { StopAfterTargetExit = true, TargetExitDelay = TimeSpan.FromSeconds(10), MinimumFreeBytes = 1 }, TimeSpan.Zero, 0, 1000, true, TimeSpan.FromSeconds(9)));
    [Fact] public void FreeReserveTriggers() => Assert.Equal(StopReason.FreeSpaceReserveReached, _evaluator.Evaluate(new() { MinimumFreeBytes = 100 }, TimeSpan.Zero, 0, 100, false, null));
}

public sealed class FileNameTests
{
    [Fact]
    public void TokensAreInvariantAndTraversalIsRemoved()
    {
        var value = FileNameTemplate.Expand("../{AppName}_{DateTime}_{SessionId}", new("bad:name", "p", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 10, new DateTimeOffset(2026,8,4,20,15,30,TimeSpan.Zero)));
        Assert.DoesNotContain("..", value); Assert.DoesNotContain('/', value); Assert.Contains("2026-08-04_20-15-30", value);
    }
    [Fact] public void ReservedNameIsPrefixed() => Assert.Equal("_CON", FileNameTemplate.Expand("CON", new("a","p",Guid.NewGuid(),null,DateTimeOffset.Now)));
    [Fact] public void ReservedDeviceNameBeforeDotIsPrefixed() => Assert.Equal("_CON.log", FileNameTemplate.Expand("CON.log", new("a","p",Guid.NewGuid(),null,DateTimeOffset.Now)));
    [Fact] public void UnknownTokenIsRejected() => Assert.Throws<ArgumentException>(() => FileNameTemplate.Expand("{AppNmae}_{Date}", new("a","p",Guid.NewGuid(),null,DateTimeOffset.Now)));
    [Fact] public void TruncatedNameDoesNotEndWithDotOrSpace()
    {
        var value=FileNameTemplate.Expand(new string('a',179)+" .tail",new("a","p",Guid.NewGuid(),null,DateTimeOffset.Now));
        Assert.True(value.Length<=180); Assert.False(value.EndsWith('.')); Assert.False(value.EndsWith(' '));
    }
}

public sealed class ProfileValidationTests
{
    [Theory]
    [InlineData("service", "service.exe")]
    [InlineData("SERVICE.EXE", "SERVICE.EXE")]
    public void ProcessNamesAreNormalized(string input, string expected) => Assert.Equal(expected, ProfileValidator.NormalizeProcessName(input));
    [Fact] public void PathLikeProcessNameIsRejected() => Assert.False(ProfileValidator.IsValidProcessName("..\\service.exe"));
    [Fact]
    public void MonitoringOnlyDoesNotRequireTargetExecutable()
    {
        var profile = new CaptureProfile { LaunchTarget = false, Stop = new StopOptions { StopAfterTargetExit = false } };
        var issues = new ProfileValidator().Validate(profile);
        Assert.DoesNotContain(issues, issue => issue.Field == nameof(CaptureProfile.TargetPath));
    }
    [Fact]
    public void NetworkPathIsAcceptedAsCaptureDirectory()
    {
        var profile = new CaptureProfile { LaunchTarget = false, LocalDirectory = @"\\server\share", FileNameTemplate = "{Date}" };
        var issues = new ProfileValidator().Validate(profile);
        Assert.DoesNotContain(issues, issue => issue.Field == nameof(CaptureProfile.LocalDirectory));
    }
    [Fact]
    public void UnknownFileNameTokenIsReported()
    {
        var profile = new CaptureProfile { LaunchTarget = false, LocalDirectory = Path.GetTempPath(), FileNameTemplate = "{AppNmae}" };
        var issues = new ProfileValidator().Validate(profile);
        Assert.Contains(issues, issue => issue.Field == nameof(CaptureProfile.FileNameTemplate));
    }
}
