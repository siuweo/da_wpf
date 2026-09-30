using Avalonia.Threading;

namespace DUC_GUI.Models
{
    /// <summary>
    /// Thay cho System.Windows.Input.CommandManager (Avalonia không có): các ICommand đăng ký
    /// RequerySuggested, ViewModel gọi InvalidateRequerySuggested() khi điều kiện bật/tắt nút thay đổi.
    /// </summary>
    public static class CommandManager
    {
        public static event EventHandler? RequerySuggested;

        public static void InvalidateRequerySuggested() =>
            Dispatcher.UIThread.Post(() => RequerySuggested?.Invoke(null, EventArgs.Empty), DispatcherPriority.Background);
    }
}
