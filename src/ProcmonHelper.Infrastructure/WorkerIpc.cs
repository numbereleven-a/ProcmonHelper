using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProcmonHelper.Contracts;
using ProcmonHelper.Core;

namespace ProcmonHelper.Infrastructure;

internal static class PipeJson
{
    private const int MaximumMessageCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static async Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken token)
    {
        var json = value is WorkerCommand command
            ? JsonSerializer.Serialize<WorkerCommand>(command, Options)
            : JsonSerializer.Serialize(value, Options);
        await writer.WriteLineAsync(json.AsMemory(), token);
        await writer.FlushAsync(token);
    }
    public static async Task<T?> ReadAsync<T>(StreamReader reader, CancellationToken token)
    {
        var line = await ReadBoundedLineAsync(reader, token);
        return line is null ? default : JsonSerializer.Deserialize<T>(line, Options);
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), token);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (character[0] == '\n') return result.ToString();
            if (character[0] != '\r') result.Append(character[0]);
            if (result.Length > MaximumMessageCharacters)
                throw new InvalidDataException("Worker IPC message exceeds the 1 MiB limit.");
        }
    }
}

internal sealed class PipeStreamWriter(Stream stream) : StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
{
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        catch (IOException) when (disposing) { }
    }
}

public sealed class ElevatedWorkerHost(IProcmonController procmon, IDiskSpaceService disk, StopConditionEvaluator evaluator, IClock clock,
    ProfileValidator validator)
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(15);

    public async Task<int> RunAsync(string pipeName, Guid expectedSessionId, CancellationToken cancellationToken)
    {
        // The server ACL already limits access to this Windows user. CurrentUserOnly on the
        // client additionally validates the server owner and rejects a legitimate UAC boundary.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(20_000, cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new PipeStreamWriter(pipe) { AutoFlush = true };
        StartCaptureCommand? start = null;
        int? procmonPid = null;
        var procmonStarted = false;
        Process? targetProcess = null;
        var reason = StopReason.None;
        string? error = null;
        try
        {
            start = await PipeJson.ReadAsync<StartCaptureCommand>(reader, cancellationToken)
                ?? throw new InvalidOperationException("Worker did not receive a start command.");
            if (start.SessionId != expectedSessionId) throw new InvalidOperationException("Worker session identifier mismatch.");
            var validationErrors = validator.Validate(start.Profile).Where(x => !x.IsWarning).ToArray();
            if (validationErrors.Length > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors.Select(x => x.Message)));
            ValidateBackingPath(start.BackingFile);
            if (IsProcmonRunning())
                throw new InvalidOperationException("Process Monitor is already running. Close the existing instance and retry; ProcmonHelper will not terminate it automatically.");

            DateTimeOffset? startedAt = null;
            DateTimeOffset? targetExitedAt = null;
            int? targetPid = null;
            var lastClientContact = Stopwatch.GetTimestamp();
            Task? progressWrite = null;
            var effectiveProfile = start.Profile;
            if (effectiveProfile.ExcludeProcmon && effectiveProfile.FilterMode != FilterMode.PmcConfiguration)
            {
                var configurationPath = BuiltInProcmonConfiguration.WriteToDirectory(Path.GetDirectoryName(start.BackingFile)!);
                effectiveProfile = effectiveProfile with { FilterMode = FilterMode.PmcConfiguration, PmcPath = configurationPath };
            }
            procmonPid = await procmon.StartAsync(effectiveProfile, start.BackingFile, cancellationToken);
            procmonStarted = true;
            await PipeJson.WriteAsync(writer, new WorkerEvent("started", start.SessionId, "Process Monitor started.", procmonPid), cancellationToken);
            await procmon.WaitUntilReadyAsync(effectiveProfile.ProcmonPath, TimeSpan.FromSeconds(30), cancellationToken);
            if (!start.Profile.LaunchTarget) startedAt = clock.Now;
            await PipeJson.WriteAsync(writer, new WorkerEvent("ready", start.SessionId, "Process Monitor is ready.", procmonPid), cancellationToken);
            var readTask = PipeJson.ReadAsync<WorkerCommand>(reader, cancellationToken);
            while (reason == StopReason.None)
            {
                var delay = Task.Delay(500, cancellationToken);
                var completed = await Task.WhenAny(readTask, delay);
                if (completed == readTask)
                {
                    var command = await readTask;
                    if (command is null) { reason = StopReason.ConnectionLost; break; }
                    if (command.SessionId != start.SessionId) throw new InvalidOperationException("IPC session identifier mismatch.");
                    lastClientContact = Stopwatch.GetTimestamp();
                    switch (command)
                    {
                        case SetTargetPidCommand target:
                            targetPid = target.TargetPid;
                            targetProcess?.Dispose();
                            try
                            {
                                targetProcess = Process.GetProcessById(target.TargetPid);
                                if (Math.Abs((targetProcess.StartTime.ToUniversalTime() - target.TargetStartedAt.UtcDateTime).TotalSeconds) > 0.001)
                                {
                                    targetProcess.Dispose();
                                    targetProcess = null;
                                    targetExitedAt ??= clock.Now;
                                }
                            }
                            catch (ArgumentException) { targetProcess = null; targetExitedAt ??= clock.Now; }
                            startedAt ??= clock.Now;
                            break;
                        case StopCaptureCommand stop: reason = stop.Reason; break;
                        case HeartbeatCommand: break;
                    }
                    readTask = PipeJson.ReadAsync<WorkerCommand>(reader, cancellationToken);
                }

                if (Stopwatch.GetElapsedTime(lastClientContact) > ClientTimeout)
                {
                    reason = StopReason.ConnectionLost;
                    break;
                }
                if (procmonPid is { } runningProcmonPid && !IsRunning(runningProcmonPid))
                {
                    reason = StopReason.ProcmonExited;
                    break;
                }

                long pmlBytes;
                long freeBytes;
                try
                {
                    pmlBytes = EnumeratePml(start.BackingFile).Sum(SafeLength);
                    freeBytes = disk.GetFreeBytes(Path.GetDirectoryName(start.BackingFile)!);
                }
                catch (IOException ex)
                {
                    reason = StopReason.Error;
                    error = $"Capture storage could not be checked: {ex.Message}";
                    break;
                }
                var targetExited = targetPid is not null && (targetProcess is null || HasExited(targetProcess));
                if (targetExited && targetExitedAt is null) targetExitedAt = clock.Now;
                var elapsed = startedAt is null ? TimeSpan.Zero : clock.Now - startedAt.Value;
                reason = reason == StopReason.None
                    ? evaluator.Evaluate(start.Profile.Stop, elapsed, pmlBytes, freeBytes, targetExited, targetExitedAt is null ? null : clock.Now - targetExitedAt)
                    : reason;
                if (progressWrite is { IsCompleted: true })
                {
                    try { await progressWrite; }
                    catch (IOException) { reason = StopReason.ConnectionLost; break; }
                    progressWrite = null;
                }
                if (progressWrite is null)
                    progressWrite = PipeJson.WriteAsync(writer, new WorkerEvent("progress", start.SessionId, startedAt is null ? "Waiting for target application." : "Capturing", procmonPid, reason, pmlBytes, freeBytes), cancellationToken);
            }

            if (progressWrite is not null)
            {
                try { await progressWrite.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (TimeoutException) { reason = StopReason.ConnectionLost; }
            }
        }
        catch (IOException ex) when (IsPipeDisconnect(ex)) { reason = StopReason.ConnectionLost; }
        catch (IOException ex) { reason = StopReason.Error; error = $"Capture I/O failed: {ex.Message}"; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { reason = StopReason.ConnectionLost; }
        catch (Exception ex)
        {
            reason = StopReason.Error;
            error = ex.Message;
        }
        finally
        {
            targetProcess?.Dispose();
            if (procmonStarted && start is not null)
            {
                using var notification = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await PipeJson.WriteAsync(writer, new WorkerEvent("stopping", expectedSessionId, "Stopping Process Monitor.", procmonPid, reason), notification.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await procmon.StopAsync(start.Profile.ProcmonPath, TimeSpan.FromSeconds(20), cleanup.Token); }
                catch (Exception ex)
                {
                    reason = StopReason.Error;
                    error = error is null ? $"Process Monitor could not be stopped: {ex.Message}" : $"{error} Process Monitor could not be stopped: {ex.Message}";
                }
            }
        }

        var kind = error is null ? "stopped" : "error";
        var message = error ?? "Process Monitor stopped.";
        using var finalNotification = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await PipeJson.WriteAsync(writer, new WorkerEvent(kind, expectedSessionId, message, procmonPid, reason), finalNotification.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return error is null ? 0 : 1;
    }

    private static IEnumerable<string> EnumeratePml(string backingFile)
    {
        var directory = Path.GetDirectoryName(backingFile)!;
        var stem = Path.GetFileNameWithoutExtension(backingFile);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, stem + "*.pml") : [];
    }
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
    private static bool IsPipeDisconnect(IOException exception) => (exception.HResult & 0xffff) is 109 or 232 or 233;
    private static bool IsProcmonRunning()
    {
        var processes = Process.GetProcessesByName("Procmon64").Concat(Process.GetProcessesByName("Procmon")).ToArray();
        try { return processes.Any(x => !x.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void ValidateBackingPath(string backingFile)
    {
        var backing = Path.GetFullPath(backingFile);
        var directory = Path.GetDirectoryName(backing) ?? throw new InvalidOperationException("Backing PML directory is missing.");
        if (!Path.IsPathFullyQualified(backingFile) || !string.Equals(Path.GetExtension(backing), ".pml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backing file must be an absolute .pml path.");
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var directoryWithSeparator = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (protectedRoots.Any(root => directoryWithSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Backing files cannot be written below a protected system directory.");
        RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
    }

    private static void RejectReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Backing-file directory cannot contain links or reparse points.");
            current = current.Parent;
        }
    }
}

public sealed class ElevatedWorkerClient(ITargetProcessLauncher targetLauncher, string? workerExecutablePath = null) : IElevatedWorkerClient
{
    public async Task<ElevatedCaptureResult> CaptureAsync(
        Guid sessionId, CaptureProfile profile, string backingFile,
        IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
    {
        var pipeName = $"ProcmonHelper-{sessionId:N}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))}";
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("Unable to determine the current Windows user.");
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetOwner(userSid);
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(administratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, pipeSecurity, HandleInheritability.None);
        var executable = workerExecutablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the application executable.");
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        startInfo.ArgumentList.Add("--elevated-worker");
        startInfo.ArgumentList.Add("--pipe"); startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--session"); startInfo.ArgumentList.Add(sessionId.ToString("D"));
        using var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the elevated worker.");
        var connected = pipe.WaitForConnectionAsync(cancellationToken);
        var exited = worker.WaitForExitAsync(cancellationToken);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), connectTimeout.Token);
        var first = await Task.WhenAny(connected, exited, timeoutTask);
        connectTimeout.Cancel();
        if (first == exited)
            throw new InvalidOperationException($"Elevated worker exited before connecting (code {worker.ExitCode}).");
        if (first != connected)
            throw new TimeoutException("Elevated worker did not connect within 30 seconds.");
        await connected;
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new PipeStreamWriter(pipe) { AutoFlush = true };
        await PipeJson.WriteAsync(writer, new StartCaptureCommand(sessionId, profile, backingFile), cancellationToken);

        WorkerEvent evt;
        while (true)
        {
            evt = await PipeJson.ReadAsync<WorkerEvent>(reader, cancellationToken) ?? throw new IOException("Elevated worker disconnected before Process Monitor became ready.");
            if (evt.Kind == "error") throw new InvalidOperationException(evt.Message);
            if (evt.Kind == "stopped") throw new InvalidOperationException("Elevated worker stopped before Process Monitor became ready.");
            if (evt.Kind == "started") progress?.Report(new(CaptureState.StartingProcmon, evt.Message, TimeSpan.Zero, 0, 0, null));
            if (evt.Kind == "ready") break;
        }

        progress?.Report(new(CaptureState.WaitingForProcmon, evt.Message, TimeSpan.Zero, 0, 0, null));
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(WorkerCommand command, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await writeGate.WaitAsync(timeout.Token);
                try { await PipeJson.WriteAsync(writer, command, timeout.Token); }
                finally { writeGate.Release(); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Elevated-worker IPC write timed out.");
            }
        }
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                await Task.Delay(1000, heartbeatCts.Token);
                await SendAsync(new HeartbeatCommand(sessionId), heartbeatCts.Token);
            }
        }, heartbeatCts.Token);
        int? targetPid = null;
        try
        {
        if (profile.LaunchTarget)
        {
            progress?.Report(new(CaptureState.LaunchingTarget, "Launching target application.", TimeSpan.Zero, 0, 0, null));
            try
            {
                var launchedTarget = await Task.Run(() => targetLauncher.LaunchAsync(profile, cancellationToken), CancellationToken.None);
                targetPid = launchedTarget.ProcessId;
                await SendAsync(new SetTargetPidCommand(sessionId, targetPid.Value, launchedTarget.StartedAt), CancellationToken.None);
            }
            catch
            {
                try { await SendAsync(new StopCaptureCommand(sessionId, StopReason.Error), CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
                throw;
            }
        }
        var started = DateTimeOffset.Now;
        var captureMessage = profile.LaunchTarget ? "Capturing" : "Capturing without launching a target application.";
        progress?.Report(new(CaptureState.Capturing, captureMessage, TimeSpan.Zero, 0, 0, targetPid));
        var stopSent = false;
        var read = PipeJson.ReadAsync<WorkerEvent>(reader, CancellationToken.None);
        while (true)
        {
            if (cancellationToken.IsCancellationRequested && !stopSent)
            {
                stopSent = true;
                    try { await SendAsync(new StopCaptureCommand(sessionId, StopReason.Manual), CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    // The worker may already be finalizing for another reason. Consume its queued
                    // stopping/stopped event instead of replacing a valid capture with Pipe is broken.
                }
            }
            using var heartbeatDelay = new CancellationTokenSource();
            var delayTask = Task.Delay(100, heartbeatDelay.Token);
            var completed = await Task.WhenAny(read, delayTask);
            if (completed != read)
            {
                continue;
            }
            heartbeatDelay.Cancel();
            evt = await read ?? throw new IOException("Elevated worker disconnected.");
            if (evt.Kind == "error") throw new InvalidOperationException(evt.Message);
            if (evt.Kind == "stopped")
                return new(targetPid, evt.StopReason, started, evt.ProcmonPid);
            if (evt.Kind == "stopping")
            {
                progress?.Report(new(CaptureState.StoppingProcmon, evt.Message, DateTimeOffset.Now - started, evt.PmlBytes, evt.FreeBytes, targetPid, evt.StopReason));
            }
            else
                progress?.Report(new(CaptureState.Capturing, evt.Message, DateTimeOffset.Now - started, evt.PmlBytes, evt.FreeBytes, targetPid, evt.StopReason));
            read = PipeJson.ReadAsync<WorkerEvent>(reader, CancellationToken.None);
        }
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (TimeoutException) { }
        }
    }
}
