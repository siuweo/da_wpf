using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DUC_GUI.Models;
using DUC_GUI.Services;

namespace DUC_GUI
{
    /// <summary>
    /// Cửa sổ xem camera D455 (Window > Camera): RGB và depth giả màu từ camera/camera_service.py.
    /// Chỉ nhận hình khi cửa sổ đang mở; bấm X chỉ ẩn cửa sổ và ngắt luồng.
    /// </summary>
    public partial class CameraViewWindow : Window
    {
        private readonly Channel         _color;
        private readonly Channel         _depth;
        private readonly DispatcherTimer _statusTimer;
        private bool _allowClose;

        public CameraViewWindow()
        {
            InitializeComponent();
            TxtUrl.Text = RobotParameters.CameraViewUrl;
            _color = new Channel("RGB",   "color.mjpg", ImgBothColor, ImgColor);
            _depth = new Channel("Depth", "depth.mjpg", ImgBothDepth, ImgDepth);
            _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());

            Closing += (_, e) =>
            {
                StopStreams();
                if (_allowClose) return;
                e.Cancel = true;
                Hide();
            };
        }

        /// <summary>Mở cửa sổ (hoặc đưa lên trước) và bắt đầu nhận hình.</summary>
        public void ShowStreaming(Window owner)
        {
            if (IsVisible)
            {
                Activate();
                return;
            }
            Show(owner);
            StartStreams();
        }

        /// <summary>Đóng hẳn khi thoát ứng dụng.</summary>
        public void CloseForShutdown()
        {
            _allowClose = true;
            Close();
        }

        private void StartStreams()
        {
            string baseUrl = (TxtUrl.Text ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0) baseUrl = RobotParameters.CameraViewUrl;
            if (!baseUrl.Contains("://")) baseUrl = "http://" + baseUrl;
            TxtUrl.Text = baseUrl;

            _color.Start(baseUrl);
            _depth.Start(baseUrl);
            BtnConnect.Content = "Ngắt";
            _statusTimer.Start();
            UpdateStatus();
        }

        private void StopStreams()
        {
            _color.Stop();
            _depth.Stop();
            _statusTimer.Stop();
            BtnConnect.Content = "Kết nối";
            TxtStatus.Text = "Đã ngắt camera service.";
        }

        private void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (_color.IsRunning) StopStreams();
            else StartStreams();
        }

        private void UpdateStatus() => TxtStatus.Text = $"{_color.Report()}     |     {_depth.Report()}";

        // ===================================================================================
        // Một luồng ảnh (RGB hoặc Depth) hiển thị lên một hay nhiều ô Image.
        // Giải mã JPEG trên luồng nền; luồng UI chỉ nhận ảnh MỚI NHẤT (ảnh cũ chưa kịp vẽ bị bỏ)
        // nên giao diện không bị dồn hàng khi máy chậm.
        // ===================================================================================
        private sealed class Channel
        {
            private readonly MjpegStream _stream = new();
            private readonly string      _name;
            private readonly string      _path;
            private readonly Image[]     _views;
            private Bitmap?              _shown;
            private Bitmap?              _pending;
            private int                  _queued;
            private int                  _frames;          // số ảnh đã hiển thị kể từ lần báo trạng thái trước
            private long                 _lastFrameMs;
            private volatile string      _status = "";

            public Channel(string name, string path, params Image[] views)
            {
                _name  = name;
                _path  = path;
                _views = views;
                _stream.FrameReceived += OnFrame;
                _stream.StatusChanged += s => _status = s;
            }

            public bool IsRunning => _stream.IsRunning;

            public void Start(string baseUrl) => _stream.Start($"{baseUrl}/{_path}");

            public void Stop()
            {
                _stream.Stop();
                foreach (var v in _views) v.Source = null;
                _shown?.Dispose();
                _shown = null;
                Interlocked.Exchange(ref _pending, null)?.Dispose();
                _frames = 0;
            }

            // Luồng nền
            private void OnFrame(byte[] jpeg)
            {
                Bitmap bmp;
                try
                {
                    using var ms = new MemoryStream(jpeg);
                    bmp = new Bitmap(ms);
                }
                catch
                {
                    return;   // ảnh hỏng - bỏ qua
                }
                Interlocked.Exchange(ref _pending, bmp)?.Dispose();
                if (Interlocked.Exchange(ref _queued, 1) == 0)
                    Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
            }

            // Luồng UI
            private void Apply()
            {
                Interlocked.Exchange(ref _queued, 0);
                var bmp = Interlocked.Exchange(ref _pending, null);
                if (bmp == null) return;
                if (!_stream.IsRunning)
                {
                    bmp.Dispose();   // ảnh tới muộn sau khi đã ngắt
                    return;
                }
                foreach (var v in _views) v.Source = bmp;
                _shown?.Dispose();
                _shown       = bmp;
                _lastFrameMs = Environment.TickCount64;
                _frames++;
            }

            public string Report()
            {
                int fps = _frames;
                _frames = 0;
                bool live = _shown != null && Environment.TickCount64 - _lastFrameMs < 2000;
                return live
                    ? $"{_name}: {_shown!.PixelSize.Width}x{_shown.PixelSize.Height}  {fps} fps"
                    : $"{_name}: {(_status.Length > 0 ? _status : "chưa có hình")}";
            }
        }
    }
}
