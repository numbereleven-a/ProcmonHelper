namespace ProcmonHelper.Contracts;

public interface IProcmonCommandBuilder
{
    IReadOnlyList<string> BuildStart(CaptureProfile profile, string backingFile);
    IReadOnlyList<string> BuildWaitForIdle();
    IReadOnlyList<string> BuildTerminate();
    IReadOnlyList<string> BuildExport(string pmlPath, string destinationPath, bool applyFilter);
}

public interface IProcmonCapabilityDetector
{
    Task<ProcmonCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken);
}

public interface IProcmonController
{
    Task<int> StartAsync(CaptureProfile profile, string backingFile, CancellationToken cancellationToken);
    Task WaitUntilReadyAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken);
    Task ExportAsync(string executablePath, string pmlPath, string destinationPath, bool applyFilter, CancellationToken cancellationToken);
}

public interface ITargetProcessLauncher
{
    Task<int> LaunchAsync(CaptureProfile profile, CancellationToken cancellationToken);
}

public interface IElevatedWorkerClient
{
    Task<ElevatedCaptureResult> CaptureAsync(Guid sessionId, CaptureProfile profile, string backingFile,
        IProgress<CaptureProgress>? progress, CancellationToken cancellationToken);
}

public interface ISessionManager
{
    Task<CaptureResult> CaptureAsync(CaptureProfile profile, IProgress<CaptureProgress>? progress,
        CancellationToken captureCancellationToken, CancellationToken postProcessCancellationToken = default);
}

public interface ISessionRepository
{
    Task SaveAsync(SessionRecord session, CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionRecord>> FindRecoverableAsync(CancellationToken cancellationToken);
}

public interface IProfileRepository
{
    Task<IReadOnlyList<CaptureProfile>> LoadAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken);
    Task RenameAsync(string oldName, CaptureProfile renamed, CancellationToken cancellationToken);
    Task DeleteAsync(string name, CancellationToken cancellationToken);
    Task<CaptureProfile> ImportAsync(string path, CancellationToken cancellationToken);
    Task ExportAsync(CaptureProfile profile, string path, CancellationToken cancellationToken);
}

public interface IFileTransferService
{
    Task<string> CopyAtomicAsync(string source, string destination, bool overwrite, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
}

public interface IStoragePathResolver
{
    string DataRoot { get; }
    string SessionsRoot { get; }
    string ProfilesRoot { get; }
    string LogsRoot { get; }
    string CreateSessionDirectory(Guid sessionId);
}

public interface IDiskSpaceService { long GetFreeBytes(string path); }
public interface IHashService { Task<string> Sha256Async(string path, CancellationToken cancellationToken); }
public interface IClock { DateTimeOffset Now { get; } }
