using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using ProcmonHelper.Contracts;
using Forms = System.Windows.Forms;

namespace ProcmonHelper.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF owns the window lifetime; OnClosing disposes the tray icon.")]
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private bool _closingAfterCapture;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _trayMenu = new Forms.ContextMenuStrip();
        DataContext = viewModel;
        RefreshTrayMenu();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "ProcmonHelper",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Restore();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunning && !_closingAfterCapture)
        {
            e.Cancel = true;
            _closingAfterCapture = true;
            await _viewModel.StopAndWaitForShutdownAsync();
            Close();
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        _trayMenu.Dispose();
        base.OnClosing(e);
    }

    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void RefreshTrayMenu()
    {
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add(LocalizationService.Get("TrayRestore"), null, (_, _) => Restore());
        _trayMenu.Items.Add(LocalizationService.Get("TrayStop"), null, (_, _) => { if (_viewModel.StopCommand.CanExecute(null)) _viewModel.StopCommand.Execute(null); });
        _trayMenu.Items.Add(LocalizationService.Get("TrayExit"), null, (_, _) => Close());
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        LocalizationService.Apply(_viewModel.Language);
        RefreshTrayMenu();
    }
}
