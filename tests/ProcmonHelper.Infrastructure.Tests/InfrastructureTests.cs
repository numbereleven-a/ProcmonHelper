using ProcmonHelper.Contracts;
using ProcmonHelper.Infrastructure;
using ProcmonHelper.Core;
using System.IO.Pipes;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ProcmonHelper.Infrastructure.Tests;

public sealed class ProcmonCommandBuilderTests
{
    private readonly ProcmonCommandBuilder _builder = new();
    [Fact]
    public void StartUsesArgumentTokensWithoutAcceptingEula()
    {
        var args = _builder.BuildStart(new CaptureProfile { FilterMode = FilterMode.PmcConfiguration, PmcPath = @"C:\with spaces\f.pmc" }, @"C:\logs\x.pml");
        Assert.Equal(new[] { "/Quiet", "/Minimized", "/BackingFile", @"C:\logs\x.pml", "/LoadConfig", @"C:\with spaces\f.pmc" }, args);
        Assert.DoesNotContain("/AcceptEula", args);
    }
    [Fact]
    public void AllEventsExplicitlyDisablesSavedFilters()
    {
        var args = _builder.BuildStart(new CaptureProfile { FilterMode = FilterMode.AllEvents }, @"C:\logs\x.pml");
        Assert.Contains("/NoFilter", args);
    }
    [Theory]
    [InlineData("x.csv")]
    [InlineData("x.xml")]
    public void ExportUsesSupportedSwitches(string name) => Assert.Equal(new[] { "/OpenLog", "x.pml", "/SaveApplyFilter", "/SaveAs", name }, _builder.BuildExport("x.pml", name, true));
}

public sealed class ProfileValidationTests
{
    [Fact]
    public void MissingNestedSettingsAreReportedInsteadOfThrowing()
    {
        var profile = new CaptureProfile { Stop = null!, Processes = null! };
        var issues = new ProfileValidator().Validate(profile);
        Assert.Contains(issues, issue => issue.Field == nameof(CaptureProfile.Stop));
    }
}

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ProcmonHelperTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task CopyIsAtomicAndComplete()
    {
        Directory.CreateDirectory(_root); var source=Path.Combine(_root,"source.bin"); var target=Path.Combine(_root,"out","target.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0,10000).Select(x=>(byte)x).ToArray());
        var result=await new FileTransferService().CopyAtomicAsync(source,target,false,null,CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(source),await File.ReadAllBytesAsync(result)); Assert.False(File.Exists(target+".partial"));
    }
    [Fact]
    public async Task ProfileRoundTrips()
    {
        var paths=new StoragePathResolver(_root); var repository=new JsonProfileRepository(paths); var profile=new CaptureProfile{Name="Sample",Processes=[new("test.exe")]};
        await repository.SaveAsync(profile,CancellationToken.None); var loaded=await repository.LoadAllAsync(CancellationToken.None);
        Assert.Single(loaded); Assert.Equal("test.exe",loaded[0].Processes[0].Name);
    }
    [Fact]
    public async Task ProfileRenameKeepsStableFileAndPreservesSettings()
    {
        var paths=new StoragePathResolver(_root); var repository=new JsonProfileRepository(paths);
        var original=new CaptureProfile{Name="old:name",TargetPath="target.exe",Stop=new StopOptions{MaximumPmlBytes=12345}};
        await repository.SaveAsync(original,CancellationToken.None);
        var before=Directory.GetFiles(paths.ProfilesRoot,"*.json").Single();
        await repository.RenameAsync(original.Name,original with{Name="old_name"},CancellationToken.None);
        var after=Directory.GetFiles(paths.ProfilesRoot,"*.json").Single(); var loaded=(await repository.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Equal(before,after); Assert.Equal("old_name",loaded.Name); Assert.Equal(12345,loaded.Stop.MaximumPmlBytes);
    }
    [Fact]
    public async Task ExternalSessionDirectoryIsIndexedForRecovery()
    {
        var paths=new StoragePathResolver(Path.Combine(_root,"app"));
        var repository=new JsonSessionRepository(paths,new SystemClock());
        var external=Path.Combine(_root,"captures",Guid.NewGuid().ToString("N"));
        var session=new SessionRecord{SessionId=Guid.NewGuid(),State=CaptureState.Capturing,CreatedAt=DateTimeOffset.Now,UpdatedAt=DateTimeOffset.Now,SessionDirectory=external,BackingFile=Path.Combine(external,"procmon","capture.pml")};
        await repository.SaveAsync(session,CancellationToken.None);
        var found=await repository.FindRecoverableAsync(CancellationToken.None);
        Assert.Contains(found,x=>x.SessionId==session.SessionId && x.SessionDirectory==external);
    }
    [Theory]
    [InlineData("//server/share/logs", "\\\\server\\share\\logs")]
    [InlineData("C:\\logs", "C:\\logs")]
    public void DestinationNormalizationIsSafe(string input,string expected)=>Assert.Equal(expected,SessionManager.NormalizeDestination(input));
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}

public sealed class WorkerProtocolTests
{
    [Fact]
    public async Task StartupFailureIsReturnedInsteadOfBrokenPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sessionId=Guid.NewGuid();
        var pipeName=$"ProcmonHelper-Test-{sessionId:N}";
        using var server=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var host=new ElevatedWorkerHost(new FailingProcmonController(),new FixedDisk(),new StopConditionEvaluator(),new SystemClock(),new ProfileValidator());
        var hostTask=host.RunAsync(pipeName,sessionId,CancellationToken.None);
        await server.WaitForConnectionAsync();
        using var reader=new StreamReader(server,leaveOpen:true);
        using var writer=new StreamWriter(server,leaveOpen:true){AutoFlush=true};
        var root=Path.Combine(Path.GetTempPath(),"ProcmonHelperProtocol",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source=Environment.ProcessPath!; var procmonPath=Path.Combine(root,"Procmon64.exe"); var targetPath=Path.Combine(root,"target.exe");
        File.Copy(source,procmonPath); File.Copy(source,targetPath);
        var backing=Path.Combine(root,"capture.pml");
        var profile=new CaptureProfile{ProcmonPath=procmonPath,TargetPath=targetPath,WorkingDirectory=root,LocalDirectory=root};
        var command=new StartCaptureCommand(sessionId,profile,backing);
        await writer.WriteLineAsync(JsonSerializer.Serialize(command));
        var response=JsonSerializer.Deserialize<WorkerEvent>((await reader.ReadLineAsync())!);
        Assert.NotNull(response); Assert.Equal("error",response!.Kind); Assert.Contains("root cause",response.Message);
        Assert.Equal(1,await hostTask);
    }

    private sealed class FailingProcmonController:IProcmonController
    {
        public Task<int> StartAsync(CaptureProfile profile,string backingFile,CancellationToken cancellationToken)=>throw new InvalidOperationException("expected root cause");
        public Task WaitUntilReadyAsync(string executablePath,TimeSpan timeout,CancellationToken cancellationToken)=>Task.CompletedTask;
        public Task StopAsync(string executablePath,TimeSpan timeout,CancellationToken cancellationToken)=>Task.CompletedTask;
        public Task ExportAsync(string executablePath,string pmlPath,string destinationPath,bool applyFilter,CancellationToken cancellationToken)=>Task.CompletedTask;
    }
    private sealed class FixedDisk:IDiskSpaceService{public long GetFreeBytes(string path)=>long.MaxValue;}
}

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ProcmonHelperSessionTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ManualStopFinalEventDoesNotRegressToCapturingOrDeadlockUiContext()
    {
        Directory.CreateDirectory(_root);
        var source = Environment.ProcessPath!;
        var procmon = Path.Combine(_root, "Procmon64.exe");
        var target = Path.Combine(_root, "target.exe");
        File.Copy(source, procmon);
        File.Copy(source, target);
        var paths = new StoragePathResolver(Path.Combine(_root, "app"));
        var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, new SystemClock()),
            new CompletingWorker(), new NoOpProcmonController(), new FixedCapabilities(), new FileTransferService(), new FixedDisk(), new SystemClock());
        var profile = new CaptureProfile
        {
            ProcmonPath = procmon,
            TargetPath = target,
            WorkingDirectory = _root,
            LocalDirectory = Path.Combine(_root, "captures"),
            Stop = new StopOptions { StopAfterTargetExit = false, MaximumDuration = TimeSpan.FromSeconds(1), MaximumPmlBytes = 1024 * 1024, MinimumFreeBytes = 64 * 1024 * 1024 }
        };

        var run = Task.Run(() =>
        {
            using var context = new PumpSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var capture = manager.CaptureAsync(profile, null, CancellationToken.None);
            context.RunUntilComplete(capture);
#pragma warning disable xUnit1031 // This regression test deliberately models a synchronous UI message pump.
            return capture.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        });

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CaptureState.Completed, result.Session.State);
        Assert.Single(result.Files.Where(x => string.Equals(Path.GetExtension(x), ".pml", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task OptionalExportFailurePreservesPmlAndCompletesWithWarnings()
    {
        Directory.CreateDirectory(_root);
        var source = Environment.ProcessPath!;
        var procmon = Path.Combine(_root, "Procmon64.exe");
        var target = Path.Combine(_root, "target.exe");
        File.Copy(source, procmon); File.Copy(source, target);
        var paths = new StoragePathResolver(Path.Combine(_root, "app"));
        var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, new SystemClock()),
            new CompletingWorker(), new FailingExportProcmonController(), new FixedCapabilities(), new FileTransferService(), new FixedDisk(), new SystemClock());
        var profile = new CaptureProfile
        {
            ProcmonPath = procmon, TargetPath = target, WorkingDirectory = _root, LocalDirectory = Path.Combine(_root, "captures"),
            Formats = OutputFormats.Pml | OutputFormats.Csv,
            Stop = new StopOptions { StopAfterTargetExit = false, MaximumDuration = TimeSpan.FromSeconds(1), MaximumPmlBytes = 1024 * 1024, MinimumFreeBytes = 64 * 1024 * 1024 }
        };

        var result = await manager.CaptureAsync(profile, null, CancellationToken.None);

        Assert.Equal(CaptureState.CompletedWithWarnings, result.Session.State);
        Assert.Contains(result.Session.Warnings, warning => warning.Contains("export failed", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Files.Where(path => Path.GetExtension(path).Equals(".pml", StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class CompletingWorker : IElevatedWorkerClient
    {
        public Task<ElevatedCaptureResult> CaptureAsync(Guid sessionId, CaptureProfile profile, string backingFile, IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backingFile)!);
            File.WriteAllBytes(backingFile, [1, 2, 3]);
            progress?.Report(new(CaptureState.StartingProcmon, "started", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.WaitingForProcmon, "ready", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.LaunchingTarget, "launching", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.Capturing, "capturing", TimeSpan.Zero, 3, long.MaxValue, 123));
            progress?.Report(new(CaptureState.StoppingProcmon, "stopping", TimeSpan.FromSeconds(1), 3, long.MaxValue, 123, StopReason.DurationReached));
            progress?.Report(new(CaptureState.Capturing, "late final event", TimeSpan.FromSeconds(1), 3, long.MaxValue, 123, StopReason.DurationReached));
            return Task.FromResult(new ElevatedCaptureResult(123, StopReason.DurationReached, DateTimeOffset.Now, 456));
        }
    }

    private class NoOpProcmonController : IProcmonController
    {
        public Task<int> StartAsync(CaptureProfile profile, string backingFile, CancellationToken cancellationToken) => Task.FromResult(456);
        public Task WaitUntilReadyAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public virtual Task ExportAsync(string executablePath, string pmlPath, string destinationPath, bool applyFilter, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingExportProcmonController : NoOpProcmonController
    {
        public override Task ExportAsync(string executablePath, string pmlPath, string destinationPath, bool applyFilter, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic export failure");
    }

    private sealed class FixedCapabilities : IProcmonCapabilityDetector
    {
        public Task<ProcmonCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcmonCapabilities(new Version(4, 1), new HashSet<string>(), true, true, true, true));
    }

    private sealed class FixedDisk : IDiskSpaceService { public long GetFreeBytes(string path) => long.MaxValue; }

    private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void RunUntilComplete(Task task)
        {
            while (!task.IsCompleted)
            {
                if (_queue.TryTake(out var work, 100)) work.Callback(work.State);
            }
            while (_queue.TryTake(out var remaining)) remaining.Callback(remaining.State);
        }
        public void Dispose() => _queue.Dispose();
    }
}
