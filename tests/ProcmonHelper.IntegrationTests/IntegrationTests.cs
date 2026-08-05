using ProcmonHelper.Contracts;
using ProcmonHelper.Infrastructure;
using ProcmonHelper.Core;
using System.Diagnostics;
using System.Security.Principal;

namespace ProcmonHelper.IntegrationTests;

public sealed class CapabilityIntegrationTests
{
    [Fact]
    public async Task MissingProcmonFailsWithoutLaunchingAnything()
    {
        var detector=new ProcmonCapabilityDetector();
        await Assert.ThrowsAsync<FileNotFoundException>(()=>detector.DetectAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe"),CancellationToken.None));
    }

    [Fact]
    public void IntegrationTestsDoNotRequireProcmonByDefault()
    {
        var configured=Environment.GetEnvironmentVariable("PROCMON64_PATH");
        Assert.True(string.IsNullOrEmpty(configured) || Path.IsPathFullyQualified(configured));
    }
}

public sealed class RealProcmonSmokeTests
{
    [SkippableFact]
    public async Task CapturesOwnedNotepad_WhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var procmonPath = Environment.GetEnvironmentVariable("PROCMON64_PATH");
        Skip.If(string.IsNullOrWhiteSpace(procmonPath), "PROCMON64_PATH is not configured.");

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.True(principal.IsInRole(WindowsBuiltInRole.Administrator), "Real Procmon smoke test must run elevated.");
        Assert.True(File.Exists(procmonPath));

        var root = Path.Combine(Path.GetTempPath(), "ProcmonHelperSmoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pml = Path.Combine(root, "notepad-smoke.pml");
        var profile = new CaptureProfile
        {
            ProcmonPath = procmonPath,
            TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"),
            WorkingDirectory = root,
            LocalDirectory = root,
            Stop = new StopOptions { StopAfterTargetExit = true, TargetExitDelay = TimeSpan.Zero, MaximumDuration = TimeSpan.FromSeconds(30), MaximumPmlBytes = 256 * 1024 * 1024, MinimumFreeBytes = 64 * 1024 * 1024 }
        };
        var controller = new ProcmonController(new ProcmonCommandBuilder());
        Process? notepad = null;
        try
        {
            await controller.StartAsync(profile, pml, CancellationToken.None);
            await controller.WaitUntilReadyAsync(procmonPath, TimeSpan.FromSeconds(30), CancellationToken.None);
            notepad = Process.Start(new ProcessStartInfo(profile.TargetPath) { UseShellExecute = false, WorkingDirectory = root });
            Assert.NotNull(notepad);
            await Task.Delay(2000);
            notepad!.CloseMainWindow();
            await notepad.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await controller.StopAsync(procmonPath, TimeSpan.FromSeconds(20), CancellationToken.None);
            if (notepad is { HasExited: false }) notepad.Kill(true);
        }
        await Task.Delay(1000);
        Assert.True(File.Exists(pml));
        Assert.True(new FileInfo(pml).Length > 0);
    }

    [SkippableFact]
    public async Task CapturesThroughElevatedWorker_WhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var procmonPath=Environment.GetEnvironmentVariable("PROCMON64_PATH");
        var helperPath=Environment.GetEnvironmentVariable("PROCMONHELPER_EXE");
        Skip.If(string.IsNullOrWhiteSpace(procmonPath)||string.IsNullOrWhiteSpace(helperPath), "PROCMON64_PATH and PROCMONHELPER_EXE are required.");
        Assert.True(File.Exists(procmonPath)); Assert.True(File.Exists(helperPath));
        var existing=Process.GetProcessesByName("notepad").Select(x=>x.Id).ToHashSet();
        var root=Path.Combine(Path.GetTempPath(),"ProcmonHelperWorkerSmoke",Guid.NewGuid().ToString("N"));
        var captureDirectory=Path.Combine(root,"captures");
        Directory.CreateDirectory(captureDirectory);
        var profile=new CaptureProfile
        {
            ProcmonPath=procmonPath,TargetPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","notepad.exe"),WorkingDirectory=root,LocalDirectory=captureDirectory,
            Stop=new StopOptions{StopAfterTargetExit=false,MaximumDuration=TimeSpan.FromSeconds(4),MaximumPmlBytes=256*1024*1024,MinimumFreeBytes=64*1024*1024}
        };
        try
        {
            var paths=new StoragePathResolver(Path.Combine(root,"app"));
            var clock=new SystemClock();
            var manager=new SessionManager(new ProfileValidator(),paths,new JsonSessionRepository(paths,clock),
                new ElevatedWorkerClient(new TargetProcessLauncher(),helperPath),new ProcmonController(new ProcmonCommandBuilder()),new ProcmonCapabilityDetector(),
                new FileTransferService(),new DiskSpaceService(),clock);
            var result=await manager.CaptureAsync(profile,null,CancellationToken.None);
            Assert.Equal(StopReason.DurationReached,result.Session.StopReason);
            var pml=Assert.Single(result.Files.Where(x=>string.Equals(Path.GetExtension(x),".pml",StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(Path.GetFullPath(captureDirectory),Path.GetDirectoryName(Path.GetFullPath(pml)));
            Assert.True(new FileInfo(pml).Length>0);
            Assert.Empty(Directory.EnumerateDirectories(captureDirectory));
        }
        finally
        {
            foreach(var process in Process.GetProcessesByName("notepad").Where(x=>!existing.Contains(x.Id)))
            {
                process.CloseMainWindow();
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch{if(!process.HasExited)process.Kill(true);}
            }
        }
    }

    [SkippableFact]
    public async Task ManualStopFinalizesAndSavesPml_WhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var procmonPath = Environment.GetEnvironmentVariable("PROCMON64_PATH");
        var helperPath = Environment.GetEnvironmentVariable("PROCMONHELPER_EXE");
        Skip.If(string.IsNullOrWhiteSpace(procmonPath) || string.IsNullOrWhiteSpace(helperPath), "PROCMON64_PATH and PROCMONHELPER_EXE are required.");
        Assert.True(File.Exists(procmonPath));
        Assert.True(File.Exists(helperPath));
        var existing = Process.GetProcessesByName("notepad").Select(x => x.Id).ToHashSet();
        var root = Path.Combine(Path.GetTempPath(), "ProcmonHelperManualStopSmoke", Guid.NewGuid().ToString("N"));
        var captureDirectory = Path.Combine(root, "captures");
        Directory.CreateDirectory(captureDirectory);
        var profile = new CaptureProfile
        {
            ProcmonPath = procmonPath,
            TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"),
            WorkingDirectory = root,
            LocalDirectory = captureDirectory,
            Stop = new StopOptions { StopAfterTargetExit = false, MaximumDuration = TimeSpan.FromSeconds(30), MaximumPmlBytes = 256 * 1024 * 1024, MinimumFreeBytes = 64 * 1024 * 1024 }
        };
        using var stop = new CancellationTokenSource();
        var stopScheduled = 0;
        var progress = new InlineProgress<CaptureProgress>(update =>
        {
            if (update.State == CaptureState.Capturing && Interlocked.Exchange(ref stopScheduled, 1) == 0)
                stop.CancelAfter(TimeSpan.FromSeconds(1));
        });
        try
        {
            var paths = new StoragePathResolver(Path.Combine(root, "app"));
            var clock = new SystemClock();
            var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, clock),
                new ElevatedWorkerClient(new TargetProcessLauncher(), helperPath), new ProcmonController(new ProcmonCommandBuilder()), new ProcmonCapabilityDetector(),
                new FileTransferService(), new DiskSpaceService(), clock);
            var result = await manager.CaptureAsync(profile, progress, stop.Token);
            Assert.Equal(CaptureState.Completed, result.Session.State);
            Assert.Equal(StopReason.Manual, result.Session.StopReason);
            var pml = Assert.Single(result.Files.Where(x => string.Equals(Path.GetExtension(x), ".pml", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(Path.GetFullPath(captureDirectory), Path.GetDirectoryName(Path.GetFullPath(pml)));
            Assert.True(new FileInfo(pml).Length > 0);
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("notepad").Where(x => !existing.Contains(x.Id)))
            {
                process.CloseMainWindow();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { if (!process.HasExited) process.Kill(true); }
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
