using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using DUC_GUI.Models;
using DUC_GUI.Services;
using DUC_GUI.ViewModels;

namespace DUC_GUI
{
    public partial class MainWindow : Window
    {
        private readonly IpcClient          _ipc    = new();
        private readonly CameraServer       _camera = new(RobotParameters.CameraPort);
        private readonly TelemetryCsvLogger _csv    = new();
        private readonly MainViewModel      _viewModel;
        private readonly ConsoleLogWindow   _logWindow = new();
        private readonly CameraViewWindow   _cameraWindow = new();
        private bool _closing;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel  = new MainViewModel(_ipc, _camera, _csv);
            DataContext = _viewModel;

            // Terminal Log: bấm X chỉ ẩn, mở lại qua Window > Console
            _viewModel.LogLine += _logWindow.AppendLog;
            _logWindow.Closing += (_, e) =>
            {
                if (_closing) return;
                e.Cancel = true;
                _logWindow.Hide();
            };

            Opened  += (_, _) => _camera.Start();
            Closing += OnWindowClosing;
        }

        // Đóng ứng dụng: gửi DIS để IPC dừng robot, tắt driver, đóng EtherCAT rồi mới thoát
        private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_closing) return;
            e.Cancel = true;
            _closing = true;
            try
            {
                await _viewModel.ShutdownAsync();
            }
            finally
            {
                _camera.Dispose();
                _csv.Dispose();
                _ipc.Dispose();
                _logWindow.Close();
                _cameraWindow.CloseForShutdown();
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Close();
            }
        }

        private void MenuConsole_Click(object? sender, RoutedEventArgs e)
        {
            if (_logWindow.IsVisible) _logWindow.Activate();
            else _logWindow.Show(this);
        }

        private void MenuCamera_Click(object? sender, RoutedEventArgs e) => _cameraWindow.ShowStreaming(this);
    }
}
