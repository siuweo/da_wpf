using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DUC_GUI.Services
{
    /// <summary>
    /// Kết nối TCP DUY NHẤT tới IPC (thay cho TCP 8000 + TCP 8500 + UDP 9000 trước đây).
    ///   GUI -> IPC : lệnh JSON (CON, DIS, STA, STO, HAL, RES, HOM, HAN, JOG)
    ///   IPC -> GUI : ACK từng lệnh, sự kiện DONE/ABORT, log (custom_printf), phản hồi FB 50 Hz
    /// Các event được phát trên luồng nền – bên nhận tự chuyển về luồng UI nếu cần.
    /// </summary>
    public sealed class IpcClient : IDisposable
    {
        public event Action<string>?    LogLine;            // dòng log đã gắn giờ, cho Console
        public event Action<Telemetry>? TelemetryReceived;
        public event Action<IpcAck>?    AckReceived;
        public event Action<IpcEvent>?  EventReceived;
        public event Action<bool>?      ConnectionChanged;  // true: vừa nối TCP, false: vừa mất

        private TcpClient?               _client;
        private NetworkStream?           _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim   _sendLock = new(1, 1);
        private readonly object          _stateLock = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcAck>> _pendingAcks = new();

        public bool IsConnected => _stream != null && _client?.Connected == true;

        public async Task<bool> ConnectAsync(string host, int port, int timeoutMs = 2000)
        {
            Disconnect();
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = new CancellationTokenSource(timeoutMs);
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                client.Dispose();
                Log($"[TCP] Không kết nối được IPC {host}:{port} – {ex.Message}");
                return false;
            }

            _client = client;
            _stream = client.GetStream();
            _cts    = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_stream, _cts.Token));
            Log($"[TCP] Đã kết nối IPC {host}:{port}");
            ConnectionChanged?.Invoke(true);
            return true;
        }

        public void Disconnect()
        {
            lock (_stateLock)
            {
                if (_client == null) return;
                _cts?.Cancel();
                try { _stream?.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
                _client = null;
                _stream = null;
            }
            foreach (var tcs in _pendingAcks.Values) tcs.TrySetResult(new IpcAck { Ok = false, Msg = "Mất kết nối" });
            _pendingAcks.Clear();
            ConnectionChanged?.Invoke(false);
        }

        /// <summary>Tuần tự hoá object thành JSON và gửi (1 dòng).</summary>
        public Task<bool> SendAsync(object packet) => SendLineAsync(JsonSerializer.Serialize(packet));

        public async Task<bool> SendLineAsync(string json)
        {
            var stream = _stream;
            if (stream == null)
            {
                Log($"[TCP] Chưa kết nối IPC – bỏ lệnh {json}");
                return false;
            }
            byte[] data = Encoding.UTF8.GetBytes(json + "\n");
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data).ConfigureAwait(false);
                Log($"[TX] {json}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[TCP] Lỗi gửi: {ex.Message}");
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Gửi lệnh và chờ ACK tương ứng (null nếu hết thời gian).</summary>
        public async Task<IpcAck?> RequestAsync(object packet, string cmd, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[cmd] = tcs;
            if (!await SendAsync(packet).ConfigureAwait(false))
            {
                _pendingAcks.TryRemove(cmd, out _);
                return null;
            }
            try
            {
                return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log($"[TCP] IPC không trả lời lệnh {cmd} sau {timeout.TotalSeconds:0.#} s");
                return null;
            }
            finally
            {
                _pendingAcks.TryRemove(new KeyValuePair<string, TaskCompletionSource<IpcAck>>(cmd, tcs));
            }
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length > 0) Dispatch(line);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log($"[TCP] Lỗi nhận: {ex.Message}");
            }
            if (!ct.IsCancellationRequested)
            {
                Log("[TCP] IPC đã đóng kết nối.");
                Disconnect();
            }
        }

        private void Dispatch(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                string type = doc.RootElement.TryGetProperty("T", out var t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "FB":
                        var fb = doc.RootElement.Deserialize<Telemetry>();
                        if (fb != null) TelemetryReceived?.Invoke(fb);
                        break;
                    case "LOG":
                        string msg = doc.RootElement.GetProperty("M").GetString() ?? "";
                        foreach (var l in msg.Split('\n')) Log($"[IPC] {l.TrimEnd('\r')}");
                        break;
                    case "ACK":
                        var ack = doc.RootElement.Deserialize<IpcAck>();
                        if (ack == null) break;
                        Log($"[ACK] {ack.Cmd} {(ack.Ok ? "OK" : "LỖI")} {ack.Msg}");
                        if (_pendingAcks.TryRemove(ack.Cmd, out var tcs)) tcs.TrySetResult(ack);
                        AckReceived?.Invoke(ack);
                        break;
                    case "EVT":
                        var evt = doc.RootElement.Deserialize<IpcEvent>();
                        if (evt == null) break;
                        Log($"[EVT] {evt.E} (program {evt.Id}) {evt.Msg}");
                        EventReceived?.Invoke(evt);
                        break;
                    default:
                        Log($"[RX] {line}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[TCP] Gói không hợp lệ ({ex.Message}): {line}");
            }
        }

        private void Log(string text) => LogLine?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");

        public void Dispose()
        {
            Disconnect();
            _sendLock.Dispose();
        }
    }
}
