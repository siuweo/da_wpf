using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DUC_GUI.Services
{
    /// <summary>Một lần camera phát hiện vật (toạ độ hệ robot, mm / độ, Z = mặt trên của vật).</summary>
    public sealed record CameraDetection(
        int Id, double X, double Y, double Z, double Theta,
        double? Vx, double? Vy,           // vận tốc vật nếu camera tự đo (mm/s), không có thì dùng cài đặt băng tải
        double ExtraAgeMs,                // độ trễ phía camera (age_ms / latency_ms) nếu có
        long ReceivedTicks, string Raw)
    {
        /// <summary>Tuổi của dữ liệu tính tới lúc gọi (ms) – IPC dùng để bù quãng vật đã trôi.</summary>
        public double AgeMs => (Stopwatch.GetTimestamp() - ReceivedTicks) * 1000.0 / Stopwatch.Frequency + ExtraAgeMs;
    }

    /// <summary>
    /// TCP server cổng 7500 nhận kết quả từ chương trình Python Camera (1 dòng JSON / lần phát hiện).
    /// Cổng mở suốt thời gian chạy; việc có dùng kết quả hay không do engine chương trình quyết định
    /// (chỉ nhận khi robot đang chờ vật tiếp theo).
    /// Định dạng chấp nhận:
    ///   {"camera_detect_id":1,"x":150.5,"y":-200,"z":40,"theta":30}
    ///   {"Device":"Camera","ID":1,"x":...,"y":...,"z":...,"theta":...}
    ///   (tuỳ chọn) "vx","vy" [mm/s], "age_ms" [ms]
    /// </summary>
    public sealed class CameraServer : IDisposable
    {
        public event Action<CameraDetection>? DetectionReceived;
        public event Action<string>?          LogLine;
        public event Action<int>?             ClientCountChanged;

        private readonly int             _port;
        private TcpListener?             _listener;
        private CancellationTokenSource? _cts;
        private int                      _clients;

        public CameraServer(int port) => _port = port;

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                Log($"[CAMERA] Đang lắng nghe cổng {_port}.");
                _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            }
            catch (Exception ex)
            {
                Log($"[CAMERA] Không mở được cổng {_port}: {ex.Message}");
                _listener = null;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
            ClientCountChanged?.Invoke(Interlocked.Increment(ref _clients));
            Log($"[CAMERA] {peer} đã kết nối.");
            try
            {
                using (client)
                using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (TryParse(line, out var det))
                            DetectionReceived?.Invoke(det);
                        else
                            Log($"[CAMERA] Gói không hợp lệ, bỏ qua: {line}");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log($"[CAMERA] Lỗi: {ex.Message}");
            }
            catch (OperationCanceledException) { }
            ClientCountChanged?.Invoke(Interlocked.Decrement(ref _clients));
            Log($"[CAMERA] {peer} đã ngắt.");
        }

        public static bool TryParse(string json, out CameraDetection detection)
        {
            detection = null!;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Device", out var dev) && dev.GetString() != "Camera")
                    return false;

                double? Num(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (!root.TryGetProperty(key, out var el)) continue;
                        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
                        if (el.ValueKind == JsonValueKind.String &&
                            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                            return d;
                    }
                    return null;
                }

                double? id = Num("camera_detect_id", "ID", "id");
                double? x = Num("x", "X"), y = Num("y", "Y"), z = Num("z", "Z"), th = Num("theta", "Theta");
                if (id == null || x == null || y == null || z == null || th == null)
                    return false;

                detection = new CameraDetection((int)id.Value, x.Value, y.Value, z.Value, th.Value,
                                                Num("vx", "Vx"), Num("vy", "Vy"),
                                                Num("age_ms", "latency_ms") ?? 0.0,
                                                Stopwatch.GetTimestamp(), json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Log(string text) => LogLine?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");

        public void Dispose() => Stop();
    }
}
