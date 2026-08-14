using System.Text.Json.Serialization;

namespace ProcmonHelper.Contracts;

public enum CaptureState
{
    Idle, Validating, Preparing, WaitingForElevation, StartingProcmon,
    WaitingForProcmon, LaunchingTarget, Capturing, StopRequested,
    StoppingProcmon, Finalizing, Exporting, Copying, Completed,
    CompletedWithWarnings, Failed, Recovering
}

public enum FilterMode { AllEvents, SelectedProcesses, PmcConfiguration }

[Flags]
public enum OutputFormats { Pml = 1, Csv = 2, Xml = 4 }

public enum LanguagePreference { Automatic, Russian, English }

public enum StopReason
{
    None, Manual, DurationReached, TargetExited, SizeLimitReached,
    FreeSpaceReserveReached, ConnectionLost, ProcmonExited, Error
}

public sealed record TrackedProcess(string Name, bool Enabled = true);

public sealed record StopOptions
{
    public bool StopAfterTargetExit { get; init; } = true;
    public TimeSpan TargetExitDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan? MaximumDuration { get; init; }
    public long? MaximumPmlBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}

public sealed record CaptureProfile
{
    public int SchemaVersion { get; init; } = 3;
    public string Name { get; init; } = "Default";
    public LanguagePreference Language { get; init; } = LanguagePreference.Automatic;
    public string ProcmonPath { get; init; } = string.Empty;
    public bool LaunchTarget { get; init; } = true;
    public string TargetPath { get; init; } = string.Empty;
    public string TargetArguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public bool RunTargetElevated { get; init; }
    public FilterMode FilterMode { get; init; } = FilterMode.AllEvents;
    public string PmcPath { get; init; } = string.Empty;
    public IReadOnlyList<TrackedProcess> Processes { get; init; } = [];
    public bool AutoIncludeTargetProcess { get; init; } = true;
    public bool ExcludeProcmon { get; init; } = true;
    public StopOptions Stop { get; init; } = new();
    public OutputFormats Formats { get; init; } = OutputFormats.Pml;
    public string LocalDirectory { get; init; } = string.Empty;
    public string DestinationDirectory { get; init; } = string.Empty;
    public string FileNameTemplate { get; init; } = "{AppName}_{ComputerName}_{DateTime}";
    public bool OverwriteExisting { get; init; }
    public bool Topmost { get; init; }
}

public sealed record SessionRecord
{
    public Guid SessionId { get; init; }
    public CaptureState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CaptureStartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string SessionDirectory { get; init; } = string.Empty;
    public string BackingFile { get; init; } = string.Empty;
    public string DestinationDirectory { get; init; } = string.Empty;
    public int? ProcmonPid { get; init; }
    public int? TargetPid { get; init; }
    public StopReason StopReason { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CaptureProgress(
    CaptureState State,
    string Message,
    TimeSpan Elapsed,
    long PmlBytes,
    long FreeBytes,
    int? TargetPid,
    StopReason StopReason = StopReason.None,
    double? TransferPercent = null);

public sealed record CaptureResult(SessionRecord Session, IReadOnlyList<string> Files);
public sealed record ElevatedCaptureResult(int? TargetPid, StopReason Reason, DateTimeOffset CaptureStartedAt, int? ProcmonPid);
public sealed record LaunchedTarget(int ProcessId, DateTimeOffset StartedAt);

public sealed record ProcmonCapabilities(Version Version);

public sealed record FileTransferProgress(long BytesCopied, long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 100 : BytesCopied * 100d / TotalBytes;
}

public sealed record ValidationIssue(string Field, string Message, bool IsWarning = false);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartCaptureCommand), "start")]
[JsonDerivedType(typeof(SetTargetPidCommand), "target")]
[JsonDerivedType(typeof(StopCaptureCommand), "stop")]
[JsonDerivedType(typeof(HeartbeatCommand), "heartbeat")]
public abstract record WorkerCommand(Guid SessionId);

public sealed record StartCaptureCommand(Guid SessionId, CaptureProfile Profile, string BackingFile) : WorkerCommand(SessionId);
public sealed record SetTargetPidCommand(Guid SessionId, int TargetPid, DateTimeOffset TargetStartedAt) : WorkerCommand(SessionId);
public sealed record StopCaptureCommand(Guid SessionId, StopReason Reason) : WorkerCommand(SessionId);
public sealed record HeartbeatCommand(Guid SessionId) : WorkerCommand(SessionId);

public sealed record WorkerEvent(string Kind, Guid SessionId, string Message, int? ProcmonPid = null, StopReason StopReason = StopReason.None, long PmlBytes = 0, long FreeBytes = 0);
