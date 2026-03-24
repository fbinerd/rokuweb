using System;
using System.IO;

namespace WindowManager.App.Runtime;

public static class AppDataPaths
{
    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowManagerBroadcast");

    public static string CefRoot => Path.Combine(Root, "cef");

    public static string CefBrowserProfilesRoot => Path.Combine(CefRoot, "profiles");

    public static string LogsRoot => Path.Combine(Root, "logs");

    public static string CrashesRoot => Path.Combine(Root, "crashes");

    public static string AppLogPath => Path.Combine(LogsRoot, "super.log");

    public static string WatchdogRoot => Path.Combine(Root, "watchdog");

    public static string GetWatchdogExitMarkerPath(string token) =>
        Path.Combine(WatchdogRoot, string.Format("exit-{0}.ok", token));

    public static string GetCrashLogPath(DateTime timestampUtc) =>
        Path.Combine(CrashesRoot, string.Format("crash-{0:yyyyMMdd-HHmmssfff}.log", timestampUtc));
}
