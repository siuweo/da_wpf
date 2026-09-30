using System.Collections.Concurrent;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DUC_GUI
{
    public partial class ConsoleLogWindow : Window
    {
        private const int MaxChars = 400_000;   // giữ log gọn để ô chữ không chậm dần

        // Log dồn dập (hàng nghìn dòng/giây) được gom lại, vẽ lên màn hình 10 lần/giây
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly DispatcherTimer         _flushTimer;
        private readonly StringBuilder           _text = new();

        public ConsoleLogWindow()
        {
            InitializeComponent();
            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Flush());
            _flushTimer.Start();
            Closed += (_, _) => _flushTimer.Stop();
        }

        /// <summary>Nhận log từ bất kỳ luồng nào (IPC, Camera, GUI) – không chặn luồng gọi.</summary>
        public void AppendLog(string message) => _pending.Enqueue(message);

        private void Flush()
        {
            if (_pending.IsEmpty) return;
            var batch = new StringBuilder();
            while (batch.Length < MaxChars && _pending.TryDequeue(out var line))
                batch.Append(line);

            if (_text.Length + batch.Length > MaxChars)
                _text.Remove(0, _text.Length - Math.Min(_text.Length, MaxChars / 2));
            _text.Append(batch);
            TxtConsoleLog.Text = _text.ToString();
            if (IsVisible) LogScroll.ScrollToEnd();
        }
    }
}
