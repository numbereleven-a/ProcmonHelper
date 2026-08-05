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
    private bool _closingAfterCapture;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ProcmonHelper", null, (_, _) => Restore());
        menu.Items.Add("Stop", null, (_, _) => { if (_viewModel.StopCommand.CanExecute(null)) _viewModel.StopCommand.Execute(null); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "ProcmonHelper",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Restore();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunning && !_closingAfterCapture)
        {
            e.Cancel = true;
            await _viewModel.StopAndWaitForShutdownAsync();
            _closingAfterCapture = true;
            Close();
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosing(e);
    }

    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e) => LocalizationService.Apply(_viewModel.Language);
}
