using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DUC_GUI
{
    public enum DialogIcon { Info, Warning, Error }

    /// <summary>Thay cho MessageBox / OpenFileDialog / SaveFileDialog của WPF (Avalonia không có sẵn MessageBox).</summary>
    public static class Dialogs
    {
        public static readonly FilePickerFileType TextFiles = new("Text files (*.txt)")
        {
            Patterns  = ["*.txt"],
            MimeTypes = ["text/plain"]
        };

        private static Window? MainWindow =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        /// <summary>Hộp chọn file của hệ điều hành (Windows / GTK / portal trên Linux).</summary>
        public static IStorageProvider? Storage => MainWindow?.StorageProvider;

        public static async Task ShowAsync(string text, string title, DialogIcon icon = DialogIcon.Info)
        {
            var win = new Window
            {
                Title                 = title,
                SizeToContent         = SizeToContent.WidthAndHeight,
                CanResize             = false,
                ShowInTaskbar         = false,
                MinWidth              = 320,
                MaxWidth              = 640,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var ok = new Button { Content = "OK", Width = 90, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            ok.Click += (_, _) => win.Close();

            var body = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
            DockPanel.SetDock(ok, Dock.Bottom);
            body.Children.Add(ok);
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 12,
                Margin      = new Thickness(0, 0, 0, 16),
                Children =
                {
                    new TextBlock
                    {
                        Text = icon switch { DialogIcon.Warning => "⚠", DialogIcon.Error => "⛔", _ => "ℹ" },
                        FontSize = 26,
                        Foreground = icon switch
                        {
                            DialogIcon.Warning => Brushes.DarkOrange,
                            DialogIcon.Error   => Brushes.Firebrick,
                            _                  => Brushes.SteelBlue
                        }
                    },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 540, VerticalAlignment = VerticalAlignment.Center }
                }
            });
            win.Content = body;

            if (MainWindow is { IsVisible: true } owner)
                await win.ShowDialog(owner);
            else
            {
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.Show();
            }
        }
    }
}
