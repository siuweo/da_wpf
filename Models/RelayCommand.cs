using System.Windows.Input;

namespace DUC_GUI.Models
{
    /// <summary>
    /// ICommand nhận Action không tham số (+ điều kiện bật/tắt nút) – dùng cho Jogging và các lệnh nhanh.
    /// </summary>
    internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
