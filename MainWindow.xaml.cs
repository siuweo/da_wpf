using System.ComponentModel;
using System.Windows;
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
        private bool _closing;

        public MainWindow()
        {
            InitializeComponent();

            var selector = (ProgramBlockTemplateSelector)Resources["BlockSelector"];
            selector.MoveTemplate       = (DataTemplate)Resources["MoveBlockTemplate"];
            selector.DelayTemplate      = (DataTemplate)Resources["DelayBlockTemplate"];
            selector.GpioTemplate       = (DataTemplate)Resources["GpioBlockTemplate"];
            selector.IfBranchTemplate   = (DataTemplate)Resources["IfBranchBlockTemplate"];
            selector.ElseBranchTemplate = (DataTemplate)Resources["ElseBranchBlockTemplate"];
            selector.EndIfTemplate      = (DataTemplate)Resources["EndIfBlockTemplate"];

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

            Loaded += (_, _) =>
            {
                _logWindow.Owner = this;
                if (RobotViewport.RenderHost is { } host)
                {
                    var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(RobotParameters.ViewportBackground);
                    host.ClearColor = new HelixToolkit.Maths.Color4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
                }
                _camera.Start();
            };

            Closing += OnWindowClosing;
        }

        // Đóng ứng dụng: gửi DIS để IPC dừng robot, tắt driver, đóng EtherCAT rồi mới thoát
        private async void OnWindowClosing(object? sender, CancelEventArgs e)
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
                Application.Current.Shutdown();
            }
        }

        private void MenuConsole_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow.IsVisible) _logWindow.Activate();
            else _logWindow.Show();
        }
    }
}
