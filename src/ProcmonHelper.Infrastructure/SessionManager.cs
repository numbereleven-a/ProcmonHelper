using ProcmonHelper.Contracts;
using ProcmonHelper.Core;
using System.Diagnostics;

namespace ProcmonHelper.Infrastructure;

public sealed class SessionManager(
    ProfileValidator validator,
    IStoragePathResolver paths,
    ISessionRepository sessions,
    IElevatedWorkerClient worker,
    IProcmonController procmon,
    IProcmonCapabilityDetector capabilities,
    IFileTransferService transfer,
    IDiskSpaceService disk,
    IClock clock) : ISessionManager
{
    public async Task<CaptureResult> CaptureAsync(CaptureProfile profile, IProgress<CaptureProgress>? progress,
        CancellationToken captureCancellationToken, CancellationToken postProcessCancellationToken = default)
    {
        var machine = new CaptureStateMachine();
        machine.TransitionTo(CaptureState.Validating);
        var issues = validator.Validate(profile);
        var errors = issues.Where(x => !x.IsWarning).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(x => x.Message)));
        var detected = await capabilities.DetectAsync(profile.ProcmonPath, captureCancellationToken);
        if (detected.Version == new Version(0, 0)) throw new InvalidOperationException("Unable to determine the Process Monitor version.");
        if (IsProcmonRunning())
            throw new InvalidOperationException("Process Monitor is already running. Close it before starting capture.");
        var sessionId = Guid.NewGuid();
        var sessionDirectory = paths.CreateSessionDirectory(sessionId);
        var captureDirectory = Path.GetFullPath(profile.LocalDirectory);
        Directory.CreateDirectory(captureDirectory);
        var captureTimestamp = clock.Now;
        var appName = profile.LaunchTarget ? Path.GetFileNameWithoutExtension(profile.TargetPath) : "Monitoring";
        var initialContext = new FileNameContext(appName, profile.Name, sessionId, null, captureTimestamp);
        var baseName = FileNameTemplate.Expand(profile.FileNameTemplate, initialContext);
        var backingFile = Path.Combine(sessionDirectory, "procmon", "capture.pml");
        if (disk.GetFreeBytes(captureDirectory) <= profile.Stop.MinimumFreeBytes || disk.GetFreeBytes(sessionDirectory) <= profile.Stop.MinimumFreeBytes)
            throw new IOException("Available disk space is below the configured reserve.");
        machine.TransitionTo(CaptureState.Preparing);
        var record = new SessionRecord
        {
            SessionId = sessionId, State = CaptureState.Preparing, CreatedAt = clock.Now, UpdatedAt = clock.Now,
            SessionDirectory = sessionDirectory, BackingFile = backingFile, DestinationDirectory = profile.DestinationDirectory,
            Warnings = issues.Where(x => x.IsWarning).Select(x => x.Message).ToArray()
        };
        var logPath = Path.Combine(sessionDirectory, "logs", "capture.log");
        var targetDescription = profile.LaunchTarget ? profile.TargetPath : "(not launched)";
        await AppendLogAsync(logPath, $"Session created. Procmon={profile.ProcmonPath}; Target={targetDescription}; Backing={backingFile}; Output={captureDirectory}");
        await sessions.SaveAsync(record, captureCancellationToken);
        try
        {
            machine.TransitionTo(CaptureState.WaitingForElevation);
            record = record with { State = CaptureState.WaitingForElevation };
            await sessions.SaveAsync(record, captureCancellationToken);
            var progressPersistence = Task.CompletedTask;
            var progressGate = new object();
            var managedProgress = new SynchronousProgress<CaptureProgress>(update =>
            {
                progress?.Report(update);
                if (update.State == machine.State) return;
                if (update.State == CaptureState.Capturing && machine.State is CaptureState.StopRequested or CaptureState.StoppingProcmon)
                    return;
                lock (progressGate)
                {
                    if (update.State == CaptureState.StoppingProcmon && machine.State == CaptureState.Capturing)
                        machine.TransitionTo(CaptureState.StopRequested);
                    machine.TransitionTo(update.State);
                    record = record with
                    {
                        State = update.State,
                        TargetPid = update.TargetPid ?? record.TargetPid,
                        StopReason = update.StopReason,
                        CaptureStartedAt = update.State == CaptureState.Capturing && record.CaptureStartedAt is null ? clock.Now : record.CaptureStartedAt
                    };
                    var snapshot = record;
                    var logMessage = $"State={update.State}; Elapsed={update.Elapsed}; PmlBytes={update.PmlBytes}; StopReason={update.StopReason}";
                    progressPersistence = PersistProgressAfterAsync(progressPersistence, sessions, snapshot, logPath, logMessage);
                }
            });
            var capture = await worker.CaptureAsync(sessionId, profile, backingFile, managedProgress, captureCancellationToken);
            await progressPersistence;
            machine.TransitionTo(CaptureState.Finalizing);
            record = record with { State = CaptureState.Finalizing, TargetPid = capture.TargetPid, ProcmonPid = capture.ProcmonPid, StopReason = capture.Reason, CaptureStartedAt = capture.CaptureStartedAt };
            await sessions.SaveAsync(record, CancellationToken.None);

            var pmlFiles = Directory.EnumerateFiles(Path.GetDirectoryName(backingFile)!, "*.pml").OrderBy(SegmentNumber).ThenBy(x => x).ToList();
            if (pmlFiles.Count == 0 || pmlFiles.Sum(x => new FileInfo(x).Length) == 0)
                throw new IOException("Process Monitor did not produce a non-empty PML file.");
            var files = new List<string>();
            for (var index = 0; index < pmlFiles.Count; index++)
            {
                var suffix = pmlFiles.Count > 1 ? $"_{index + 1:000}" : string.Empty;
                var target = FileNameTemplate.GetUniquePath(captureDirectory, baseName + suffix, ".pml");
                files.Add(await transfer.CopyAtomicAsync(pmlFiles[index], target, overwrite: false, null, CancellationToken.None));
            }
            var warnings = record.Warnings.ToList();
            if (capture.Reason == StopReason.ProcmonExited)
                warnings.Add("Process Monitor exited unexpectedly; the PML data written before exit was preserved.");
            if ((profile.Formats & (OutputFormats.Csv | OutputFormats.Xml)) != 0)
            {
                machine.TransitionTo(CaptureState.Exporting);
                try
                {
                    for (var index = 0; index < pmlFiles.Count; index++)
                    {
                        var suffix = pmlFiles.Count > 1 ? $"_{index + 1:000}" : string.Empty;
                        if (profile.Formats.HasFlag(OutputFormats.Csv))
                        {
                            var csv = FileNameTemplate.GetUniquePath(captureDirectory, baseName + suffix, ".csv");
                            await procmon.ExportAsync(profile.ProcmonPath, pmlFiles[index], csv, true, postProcessCancellationToken);
                            files.Add(csv);
                        }
                        if (profile.Formats.HasFlag(OutputFormats.Xml))
                        {
                            var xml = FileNameTemplate.GetUniquePath(captureDirectory, baseName + suffix, ".xml");
                            await procmon.ExportAsync(profile.ProcmonPath, pmlFiles[index], xml, true, postProcessCancellationToken);
                            files.Add(xml);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add(ex is OperationCanceledException ? "Optional CSV/XML export was cancelled." : $"Optional CSV/XML export failed: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.DestinationDirectory))
            {
                if (machine.State == CaptureState.Finalizing) machine.TransitionTo(CaptureState.Copying);
                else if (machine.State == CaptureState.Exporting) machine.TransitionTo(CaptureState.Copying);
                try
                {
                    var destination = NormalizeDestination(profile.DestinationDirectory);
                    Directory.CreateDirectory(destination);
                    var copied = new List<string>();
                    foreach (var source in files)
                    {
                        var sourceName = Path.GetFileNameWithoutExtension(source);
                        var extension = Path.GetExtension(source);
                        var target = profile.OverwriteExisting
                            ? Path.Combine(destination, sourceName + extension)
                            : FileNameTemplate.GetUniquePath(destination, sourceName, extension);
                        var transferProgress = new Progress<FileTransferProgress>(x => progress?.Report(new(CaptureState.Copying, "Copying files", TimeSpan.Zero, 0, 0, capture.TargetPid, capture.Reason, x.Percent)));
                        copied.Add(await transfer.CopyAtomicAsync(source, target, profile.OverwriteExisting, transferProgress, postProcessCancellationToken));
                    }
                    files.AddRange(copied);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add(ex is OperationCanceledException ? "Optional destination copy was cancelled." : $"Optional destination copy failed: {ex.Message}");
                }
            }
            machine.TransitionTo(warnings.Count > 0 ? CaptureState.CompletedWithWarnings : CaptureState.Completed);
            record = record with { State = machine.State, CompletedAt = clock.Now, UpdatedAt = clock.Now, Warnings = warnings };
            await sessions.SaveAsync(record, CancellationToken.None);
            await AppendLogAsync(logPath, $"Completed. State={record.State}; StopReason={record.StopReason}; Files={string.Join(" | ", files)}");
            return new CaptureResult(record, files);
        }
        catch (Exception ex)
        {
            record = record with { State = CaptureState.Failed, Error = ex.Message, CompletedAt = clock.Now };
            await sessions.SaveAsync(record, CancellationToken.None);
            await AppendLogAsync(logPath, $"Failed: {ex}");
            throw;
        }
    }

    public static string NormalizeDestination(string path) => path.StartsWith("//", StringComparison.Ordinal) ? "\\\\" + path[2..].Replace('/', '\\') : path;

    private static bool IsProcmonRunning()
    {
        var processes = Process.GetProcessesByName("Procmon64").Concat(Process.GetProcessesByName("Procmon")).ToArray();
        try { return processes.Any(x => !x.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static int SegmentNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    private static Task AppendLogAsync(string path, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static async Task PersistProgressAfterAsync(Task previous, ISessionRepository sessions, SessionRecord snapshot, string logPath, string logMessage)
    {
        await previous.ConfigureAwait(false);
        await sessions.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        await AppendLogAsync(logPath, logMessage).ConfigureAwait(false);
    }

}

internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
