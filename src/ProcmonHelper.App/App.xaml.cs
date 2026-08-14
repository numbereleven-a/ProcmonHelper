using System.Windows;
using System.IO;
using ProcmonHelper.Contracts;
using ProcmonHelper.Core;
using ProcmonHelper.Infrastructure;

namespace ProcmonHelper.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--elevated-worker", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var pipe = GetArg(e.Args, "--pipe");
                var session = Guid.Parse(GetArg(e.Args, "--session"));
                var exitCode = await ServiceRegistry.CreateWorkerHost().RunAsync(pipe, session, CancellationToken.None);
                Shutdown(exitCode);
            }
            catch (Exception ex)
            {
                try
                {
                    var logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcmonHelper", "Logs");
                    Directory.CreateDirectory(logRoot);
                    var logPath = Path.Combine(logRoot, "worker-startup.log");
                    await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}");
                }
                catch { }
                Shutdown(1);
            }
            return;
        }

        try
        {
            var services = ServiceRegistry.Create();
            LocalizationService.Apply(LanguagePreference.Automatic);
            var viewModel = new MainWindowViewModel(services.SessionManager, services.ProfileRepository, services.Paths);
            await viewModel.InitializeAsync();
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var logPath = await WriteStartupErrorAsync("app-startup.log", ex);
            System.Windows.MessageBox.Show($"ProcmonHelper could not start.\n\n{ex.Message}\n\nLog: {logPath}", "ProcmonHelper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string GetArg(string[] args, string name)
    {
        var index = args.ToList().FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Missing argument {name}.");
        return args[index + 1];
    }

    private static async Task<string> WriteStartupErrorAsync(string fileName, Exception exception)
    {
        var logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcmonHelper", "Logs");
        var logPath = Path.Combine(logRoot, fileName);
        try
        {
            Directory.CreateDirectory(logRoot);
            await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch { }
        return logPath;
    }
}

internal sealed class ServiceRegistry
{
    public required IStoragePathResolver Paths { get; init; }
    public required IProfileRepository ProfileRepository { get; init; }
    public required ISessionManager SessionManager { get; init; }

    public static ServiceRegistry Create()
    {
        IClock clock = new SystemClock();
        IStoragePathResolver paths = new StoragePathResolver();
        IDiskSpaceService disk = new DiskSpaceService();
        IProcmonCommandBuilder commands = new ProcmonCommandBuilder();
        IProcmonController procmon = new ProcmonController(commands);
        ITargetProcessLauncher target = new TargetProcessLauncher();
        var workerClient = new ElevatedWorkerClient(target);
        ISessionRepository sessionRepository = new JsonSessionRepository(paths, clock);
        IFileTransferService transfer = new FileTransferService(new HashService());
        var evaluator = new StopConditionEvaluator();
        var validator = new ProfileValidator();
        return new ServiceRegistry
        {
            Paths = paths,
            ProfileRepository = new JsonProfileRepository(paths),
            SessionManager = new SessionManager(validator, paths, sessionRepository, workerClient, procmon, new ProcmonCapabilityDetector(), transfer, disk, clock)
        };
    }

    public static ElevatedWorkerHost CreateWorkerHost()
    {
        IClock clock = new SystemClock();
        IProcmonCommandBuilder commands = new ProcmonCommandBuilder();
        IProcmonController procmon = new ProcmonController(commands);
        return new ElevatedWorkerHost(procmon, new DiskSpaceService(), new StopConditionEvaluator(), clock,
            new ProfileValidator());
    }
}
