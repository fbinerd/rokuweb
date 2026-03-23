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

    public static string WatchdogRoot => Path.Combine(Root, "watchdog");

    public static string GetWatchdogExitMarkerPath(string token) =>
        Path.Combine(WatchdogRoot, string.Format("exit-{0}.ok", token));
}
