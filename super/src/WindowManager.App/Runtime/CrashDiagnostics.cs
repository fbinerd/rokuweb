using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowManager.App.Runtime;

public static class CrashDiagnostics
{
    private static readonly object SyncRoot = new object();
    private static bool _crashReported;

    public static void Report(string source, Exception? exception, string? extraContext = null)
    {
        lock (SyncRoot)
        {
            if (_crashReported)
            {
                return;
            }

            _crashReported = true;
        }

        try
        {
            var nowUtc = DateTime.UtcNow;
            Directory.CreateDirectory(AppDataPaths.CrashesRoot);
            var path = AppDataPaths.GetCrashLogPath(nowUtc);

            var builder = new StringBuilder();
            builder.AppendLine("SuperPainel crash diagnostic");
            builder.AppendLine(string.Format("TimestampUtc: {0:O}", nowUtc));
            builder.AppendLine(string.Format("Source: {0}", source));
            builder.AppendLine(string.Format("ProcessId: {0}", Process.GetCurrentProcess().Id));
            builder.AppendLine(string.Format("ProcessName: {0}", Process.GetCurrentProcess().ProcessName));
            builder.AppendLine(string.Format("MachineName: {0}", Environment.MachineName));
            builder.AppendLine(string.Format("OSVersion: {0}", Environment.OSVersion));
            builder.AppendLine(string.Format("AppBase: {0}", AppContext.BaseDirectory));
            builder.AppendLine(string.Format("LogPath: {0}", AppDataPaths.AppLogPath));
            if (!string.IsNullOrWhiteSpace(extraContext))
            {
                builder.AppendLine();
                builder.AppendLine("Context:");
                builder.AppendLine(extraContext);
            }

            if (exception is not null)
            {
                builder.AppendLine();
                builder.AppendLine("Exception:");
                builder.AppendLine(exception.ToString());
            }

            builder.AppendLine();
            builder.AppendLine("RecentLogEntries:");
            builder.AppendLine(AppLog.GetRecentText());

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            AppLog.Write("Crash", string.Format("Diagnostico gravado em {0}", path));
        }
        catch
        {
        }
    }
}
