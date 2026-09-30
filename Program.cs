using Avalonia;

namespace DUC_GUI
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Avalonia tự chọn nền tảng theo hệ điều hành lúc chạy (Win32 / X11)
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                      .UsePlatformDetect()
                      .WithInterFont()
                      .LogToTrace();
    }
}
