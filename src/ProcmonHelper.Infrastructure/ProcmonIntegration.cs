using System.Diagnostics;
using System.Runtime.InteropServices;
using ProcmonHelper.Contracts;

namespace ProcmonHelper.Infrastructure;

public sealed class ProcmonCommandBuilder : IProcmonCommandBuilder
{
    public IReadOnlyList<string> BuildStart(CaptureProfile profile, string backingFile)
    {
        var args = new List<string> { "/Quiet", "/Minimized", "/BackingFile", backingFile };
        if (profile.FilterMode == FilterMode.AllEvents) args.Add("/NoFilter");
        if (profile.FilterMode == FilterMode.PmcConfiguration && !string.IsNullOrWhiteSpace(profile.PmcPath))
        {
            args.Add("/LoadConfig");
            args.Add(profile.PmcPath);
        }
        return args;
    }

    public IReadOnlyList<string> BuildWaitForIdle() => ["/WaitForIdle"];
    public IReadOnlyList<string> BuildTerminate() => ["/Terminate"];
    public IReadOnlyList<string> BuildExport(string pmlPath, string destinationPath, bool applyFilter)
    {
        var args = new List<string> { "/OpenLog", pmlPath };
        if (applyFilter) args.Add("/SaveApplyFilter");
        args.Add("/SaveAs");
        args.Add(destinationPath);
        return args;
    }
}

public sealed class ProcmonCapabilityDetector : IProcmonCapabilityDetector
{
    private static readonly HashSet<string> KnownSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "/OpenLog", "/BackingFile", "/NoConnect", "/NoFilter", "/AcceptEula", "/Profiling",
        "/PagingFile", "/Minimized", "/Terminate", "/Quiet", "/Run32", "/WaitForIdle",
        "/SaveAs", "/SaveAs1", "/SaveAs2", "/LoadConfig", "/SaveApplyFilter", "/Runtime",
        "/RingBuffer", "/RingBufferSize", "/RingBufferLen"
    };

    public Task<ProcmonCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Process Monitor was not found.", executablePath);
        if (!string.Equals(Path.GetFileName(executablePath), "Procmon64.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected executable must be Procmon64.exe.");
        var info = FileVersionInfo.GetVersionInfo(executablePath);
        var version = Version.TryParse(info.FileVersion?.Split(' ').FirstOrDefault(), out var parsed) ? parsed : new Version(0, 0);
        return Task.FromResult(new ProcmonCapabilities(version, KnownSwitches, true, true, true, true));
    }
}

public sealed class ProcmonController(IProcmonCommandBuilder commandBuilder) : IProcmonController
{
    public async Task<int> StartAsync(CaptureProfile profile, string backingFile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backingFile)!);
        using var process = Start(profile.ProcmonPath, commandBuilder.BuildStart(profile, backingFile));
        await Task.Delay(250, cancellationToken);
        if (process.HasExited && process.ExitCode != 0)
            throw new InvalidOperationException($"Process Monitor exited while starting (code {process.ExitCode}). The license may need to be accepted interactively.");
        return process.Id;
    }

    public async Task WaitUntilReadyAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Start(executablePath, commandBuilder.BuildWaitForIdle());
        await WaitWithTimeoutAsync(process, timeout, "Process Monitor readiness check timed out.", cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Process Monitor readiness check failed (code {process.ExitCode}).");
    }

    public async Task StopAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Start(executablePath, commandBuilder.BuildTerminate());
        await WaitWithTimeoutAsync(process, timeout, "Process Monitor termination timed out.", cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Process Monitor did not stop cleanly (code {process.ExitCode}).");
    }

    public async Task ExportAsync(string executablePath, string pmlPath, string destinationPath, bool applyFilter, CancellationToken cancellationToken)
    {
        using var process = Start(executablePath, commandBuilder.BuildExport(pmlPath, destinationPath, applyFilter));
        await WaitWithTimeoutAsync(process, TimeSpan.FromMinutes(5), "Process Monitor export timed out.", cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(destinationPath))
            throw new InvalidOperationException($"Process Monitor export failed (code {process.ExitCode}).");
    }

    private static Process Start(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Process Monitor.");
    }

    private static async Task WaitWithTimeoutAsync(Process process, TimeSpan timeout, string message, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(message);
        }
    }
}

public sealed class TargetProcessLauncher : ITargetProcessLauncher
{
    public Task<int> LaunchAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(profile.TargetPath)
        {
            UseShellExecute = profile.RunTargetElevated,
            Verb = profile.RunTargetElevated ? "runas" : string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? Path.GetDirectoryName(profile.TargetPath)!
                : profile.WorkingDirectory
        };
        foreach (var argument in WindowsCommandLineParser.Parse(profile.TargetArguments)) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start target process.");
        return Task.FromResult(process.Id);
    }
}

internal static class WindowsCommandLineParser
{
    public static IReadOnlyList<string> Parse(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var commandLine = "ProcmonHelperTarget.exe " + arguments;
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new InvalidOperationException("Target arguments could not be parsed.");
        try
        {
            var result = new List<string>(Math.Max(0, count - 1));
            for (var index = 1; index < count; index++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty);
            return result;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
