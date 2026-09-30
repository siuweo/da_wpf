using System.Windows.Input; // ICommand (có sẵn trong .NET, không thuộc WPF)

namespace DUC_GUI.Models
{
    internal sealed class RobotCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        // Use the primary constructor parameters as backing values
        private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        private readonly Predicate<object?>? _canExecute = canExecute;

        /// <summary>
        /// Kiểm tra xem tại thời điểm này nút bấm có được kích hoạt (sáng lên) hay không
        /// </summary>
        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        /// <summary>
        /// Kích hoạt hàm xử lý logic khi người dùng nhấn nút
        /// </summary>
        public void Execute(object? parameter) => _execute(parameter);

        /// <summary>
        /// Sự kiện tự động thông báo cho giao diện vẽ lại trạng thái nút (Ẩn/Hiện/Mờ) khi dữ liệu thay đổi
        /// </summary>
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}