using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace WindowManager.App.Runtime;

public static class AppLog
{
    private static readonly object SyncRoot = new object();
    private static readonly Queue<string> RecentEntries = new Queue<string>();
    private const int MaxRecentEntries = 400;

    public static ObservableCollection<string> Entries { get; } = new ObservableCollection<string>();

    public static void Write(string category, string message)
    {
        var line = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, category, message);
        PersistLine(line);
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

    public static string GetRecentText()
    {
        lock (SyncRoot)
        {
            return string.Join(Environment.NewLine, RecentEntries.ToArray());
        }
    }

    private static void PersistLine(string line)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogsRoot);
            lock (SyncRoot)
            {
                File.AppendAllText(AppDataPaths.AppLogPath, line + Environment.NewLine);
                RecentEntries.Enqueue(line);
                while (RecentEntries.Count > MaxRecentEntries)
                {
                    RecentEntries.Dequeue();
                }
            }
        }
        catch
        {
        }
    }
}
