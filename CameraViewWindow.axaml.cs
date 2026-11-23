using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DUC_GUI.Models;
using DUC_GUI.Services;

namespace DUC_GUI
{
    /// <summary>
    /// Cửa sổ xem camera D455 (Window > Camera): tách vật (RGB + viền vật, chiều cao), RGB và depth giả màu
    /// từ camera/camera_service.py.
    /// Chỉ nhận hình khi cửa sổ đang mở; bấm X chỉ ẩn cửa sổ và ngắt luồng.
    /// </summary>
    public partial class CameraViewWindow : Window
    {
        private readonly Channel         _color;
        private readonly Channel         _depth;
        private readonly Channel         _height;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _isoTimer;     // hỏi trạng thái tách vật (GET /status)
        private string _baseUrl = RobotParameters.CameraViewUrl;
        private bool   _isoPolling;
        private bool _allowClose;

        public CameraViewWindow()
        {
            InitializeComponent();
            TxtUrl.Text = RobotParameters.CameraViewUrl;
            _color  = new Channel("RGB",       "color.mjpg",  ImgIsoColor, ImgBothColor, ImgColor);
            _depth  = new Channel("Depth",     "depth.mjpg",  ImgBothDepth, ImgDepth);
            _height = new Channel("Chiều cao", "height.mjpg", ImgIsoHeight);
            _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
            _isoTimer    = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, async (_, _) => await PollIsolationAsync());

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
            _baseUrl    = baseUrl;

            _color.Start(baseUrl);
            _depth.Start(baseUrl);
            _height.Start(baseUrl);
            BtnConnect.Content = "Ngắt";
            _statusTimer.Start();
            _isoTimer.Start();
            UpdateStatus();
        }

        private void StopStreams()
        {
            _color.Stop();
            _depth.Stop();
            _height.Stop();
            _statusTimer.Stop();
            _isoTimer.Stop();
            TxtIsoState.Text = "";
            TxtObjects.Text  = "";
            BtnConnect.Content = "Kết nối";
            TxtStatus.Text = "Đã ngắt camera service.";
        }

        private void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (_color.IsRunning) StopStreams();
            else StartStreams();
        }

        private void UpdateStatus() =>
            TxtStatus.Text = $"{_color.Report()}     |     {_depth.Report()}     |     {_height.Report()}";

        // ===================================================================================
        // Tách vật: trạng thái hiệu chỉnh / độ trôi / danh sách vật (camera/object_isolation.py)
        // ===================================================================================
        private async Task PollIsolationAsync()
        {
            if (_isoPolling) return;   // lần hỏi trước chưa xong
            _isoPolling = true;
            try
            {
                var st = await CameraServiceClient.GetStatusAsync(_baseUrl);
                if (!_isoTimer.IsEnabled) return;   // đã ngắt trong lúc chờ
                ShowIsolation(st);
            }
            finally
            {
                _isoPolling = false;
            }
        }

        private void ShowIsolation(IsolationStatus? st)
        {
            if (st == null)
            {
                TxtIsoState.Text       = "Camera service không trả lời /status.";
                TxtIsoState.Foreground = Brushes.Gray;
                TxtObjects.Text        = "";
                return;
            }

            BtnCalibrate.IsEnabled = st.Calibrating == null;
            if (st.Calibrating is { } c)
            {
                TxtIsoState.Text       = $"Đang hiệu chỉnh {c.Done}/{c.Total} frame – giữ băng tải TRỐNG ...";
                TxtIsoState.Foreground = Brushes.DarkGoldenrod;
            }
            else if (!st.Calibrated)
            {
                TxtIsoState.Text       = "CHƯA HIỆU CHỈNH – để băng tải trống rồi bấm \"Hiệu chỉnh băng tải\"." +
                                         (st.Error.Length > 0 ? $"  ({st.Error})" : "");
                TxtIsoState.Foreground = Brushes.Firebrick;
            }
            else
            {
                var p = st.Plane!;
                string drift = st.DriftWarn
                    ? $"băng tải trôi {st.DriftMm:+0.0;-0.0} mm – camera bị xê dịch? Hiệu chỉnh lại!"
                    : $"trôi {st.DriftMm:+0.00;-0.00} mm";
                TxtIsoState.Text = $"Mặt phẳng {p.File} ({p.Time}): cách {p.DistanceMm:0} mm, nghiêng {p.TiltDeg:0.00}°, " +
                                   $"RMS {p.RmsMm:0.00} mm | {drift} | {st.Fps:0} fps, xử lý {st.ProcMs:0.0} ms" +
                                   (st.Error.Length > 0 ? $" | {st.Error}" : "");
                TxtIsoState.Foreground = st.DriftWarn || st.Error.Length > 0 ? Brushes.Firebrick : Brushes.DarkGreen;
            }

            if (!st.Calibrated)
            {
                TxtObjects.Text = "";
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("ĐANG TRONG ROI");
            if (st.Objects.Count == 0) sb.AppendLine("  (không có vật)");
            foreach (var o in st.Objects)
            {
                sb.AppendLine($"#{o.Id}{(o.Edge ? "  (chạm biên ROI)" : "")}");
                sb.AppendLine($"  tâm     u={o.U,6:0.0}  v={o.V,6:0.0} px");
                sb.AppendLine($"  góc     {o.AngleDeg,6:+0.0;-0.0} °");
                sb.AppendLine($"  kích th {o.LenPx:0} x {o.WidPx:0} px ({o.AreaPx} px)");
                sb.AppendLine($"  cao     {o.HMm,6:0.0} mm (frame này {o.HRawMm:0.0})");
                sb.AppendLine($"  Z đỉnh  {o.ZMm,6:0.0} mm @ ({o.PeakU}, {o.PeakV})");
                sb.AppendLine($"  v       {o.VxPx:+0.0;-0.0}, {o.VyPx:+0.0;-0.0} px/frame ({o.Frames} frame)");
                sb.AppendLine();
            }
            if (st.BeltVPx.Length == 2)
                sb.AppendLine($"Băng tải ≈ {st.BeltVPx[0]:+0.00;-0.00}, {st.BeltVPx[1]:+0.00;-0.00} px/frame").AppendLine();

            sb.AppendLine("VẬT VỪA ĐI QUA");
            if (st.Passed.Count == 0) sb.AppendLine("  (chưa có)");
            foreach (var p in st.Passed)
                sb.AppendLine($"#{p.Id,-4} {p.HMm,5:0.0} mm  ({p.P10Mm:0.0}–{p.P90Mm:0.0}, {p.N} fr)" +
                              (p.Partial ? " một phần" : ""));
            TxtObjects.Text = sb.ToString();
        }

        private async void BtnCalibrate_Click(object? sender, RoutedEventArgs e)
        {
            if (!_color.IsRunning)
            {
                TxtIsoState.Text = "Chưa kết nối camera service.";
                return;
            }
            if (!await Dialogs.ConfirmAsync(
                    "Hiệu chỉnh lại mặt phẳng băng tải?\n\nBăng tải phải TRỐNG: không có vật, không có tay trong vùng ROI " +
                    "(khung xanh) trong khoảng 1 giây.", "Hiệu chỉnh băng tải", this))
                return;
            var reply = await CameraServiceClient.CalibrateAsync(_baseUrl);
            TxtIsoState.Text       = reply.Msg;
            TxtIsoState.Foreground = reply.Ok ? Brushes.DarkGoldenrod : Brushes.Firebrick;
        }

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
