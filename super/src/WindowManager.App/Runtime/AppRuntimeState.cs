namespace WindowManager.App.Runtime;

public static class AppRuntimeState
{
    public static bool BrowserEngineAvailable { get; set; }

    public static string BrowserEngineStatusMessage { get; set; } = string.Empty;
}
