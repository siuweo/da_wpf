using System.IO;
using System.Net.Http;
using System.Text;

namespace DUC_GUI.Services
{
    /// <summary>
    /// Đọc một luồng MJPEG qua HTTP (multipart/x-mixed-replace, mỗi phần có Content-Length) từ
    /// camera/camera_service.py, vd. http://127.0.0.1:7501/color.mjpg.
    /// Mất kết nối / service chưa chạy -> tự thử lại mỗi 2 s cho tới khi Stop().
    /// Các event phát trên luồng nền.
    /// </summary>
    public sealed class MjpegStream : IDisposable
    {
        public event Action<byte[]>? FrameReceived;   // 1 ảnh JPEG nguyên vẹn
        public event Action<string>? StatusChanged;   // "Đang kết nối ...", "Đã kết nối", lỗi

        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private const int MaxFrameBytes = 16 * 1024 * 1024;

        private CancellationTokenSource? _cts;

        public string Url { get; private set; } = "";
        public bool   IsRunning => _cts != null;

        public void Start(string url)
        {
            Stop();
            Url  = url;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(url, _cts.Token));
        }

        // Không Dispose CTS: luồng nền có thể vẫn đang dùng token cho tới khi nhận ra đã huỷ
        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        private async Task RunAsync(string url, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    StatusChanged?.Invoke($"Đang kết nối {url} ...");
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token)
                                                   .ConfigureAwait(false);
                    connectTimeout.CancelAfter(Timeout.Infinite);   // đã có header: luồng chạy vô hạn
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    StatusChanged?.Invoke("Đã kết nối");
                    await ReadPartsAsync(new BufferedStream(stream, 64 * 1024), ct).ConfigureAwait(false);
                    StatusChanged?.Invoke("Camera service đã đóng luồng");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Không có hình ({ex.Message}) – thử lại sau 2 s");
                }
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Mỗi phần: "--frame" / các dòng header (có Content-Length) / dòng trống / <JPEG> / "\r\n"
        private async Task ReadPartsAsync(Stream s, CancellationToken ct)
        {
            int length = -1;
            while (!ct.IsCancellationRequested)
            {
                string? line = await ReadLineAsync(s, ct).ConfigureAwait(false);
                if (line == null) return;   // hết luồng
                if (line.Length > 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        length = int.TryParse(line[(colon + 1)..].Trim(), out int n) ? n : -1;
                    continue;
                }
                if (length <= 0) continue;   // dòng trống giữa các phần
                if (length > MaxFrameBytes) throw new InvalidDataException($"Ảnh quá lớn ({length} byte)");

                var jpeg = new byte[length];
                await s.ReadExactlyAsync(jpeg, ct).ConfigureAwait(false);
                length = -1;
                FrameReceived?.Invoke(jpeg);
            }
        }

        private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
        {
            var sb  = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                    return sb.Length > 0 ? sb.ToString() : null;
                char c = (char)one[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length > 1024) throw new InvalidDataException("Header MJPEG không hợp lệ");
                sb.Append(c);
            }
        }

        public void Dispose() => Stop();
    }
}
