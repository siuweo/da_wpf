using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DUC_GUI
{
    public partial class ConsoleLogWindow : Window
    {
        private const int MaxChars = 400_000;   // giữ log gọn để TextBox không chậm dần

        // Log dồn dập (hàng nghìn dòng/giây) được gom lại, vẽ lên TextBox 10 lần/giây
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly DispatcherTimer         _flushTimer;

        public ConsoleLogWindow()
        {
            InitializeComponent();
            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                                              (_, _) => Flush(), Dispatcher);
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

            if (TxtConsoleLog.Text.Length + batch.Length > MaxChars)
                TxtConsoleLog.Text = TxtConsoleLog.Text[^Math.Min(TxtConsoleLog.Text.Length, MaxChars / 2)..];
            TxtConsoleLog.AppendText(batch.ToString());
            if (IsVisible) TxtConsoleLog.ScrollToEnd();
        }
    }
}
