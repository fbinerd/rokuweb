using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace WindowManager.App.Runtime;

public static class AppLog
{
    public static ObservableCollection<string> Entries { get; } = new ObservableCollection<string>();

    public static void Write(string category, string message)
    {
        var line = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, category, message);
        var application = Application.Current;

        if (application?.Dispatcher is null || application.Dispatcher.CheckAccess())
        {
            Entries.Add(line);
            return;
        }

        application.Dispatcher.Invoke(() => Entries.Add(line));
    }

    public static string GetAllText()
    {
        return string.Join(Environment.NewLine, Entries.ToArray());
    }
}
