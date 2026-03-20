using System.Windows;
using System.Windows.Controls;
using WindowManager.App.Runtime;

namespace WindowManager.App;

public sealed class TransmissionLogWindow : Window
{
    public TransmissionLogWindow()
    {
        Title = "Logs de Transmissao";
        Width = 900;
        Height = 520;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();

        var copyButton = new Button
        {
            Content = "Copiar Todos",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 12, 12, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        copyButton.Click += OnCopyAllClick;
        DockPanel.SetDock(copyButton, Dock.Top);

        var listBox = new ListBox
        {
            Margin = new Thickness(12),
            ItemsSource = AppLog.Entries
        };

        root.Children.Add(copyButton);
        root.Children.Add(listBox);
        Content = root;
    }

    private static void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var text = AppLog.GetAllText();
        Clipboard.SetText(string.IsNullOrWhiteSpace(text) ? "Sem logs no momento." : text);
    }
}
