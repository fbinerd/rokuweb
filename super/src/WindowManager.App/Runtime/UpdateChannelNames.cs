using System;

namespace WindowManager.App.Runtime;

internal static class UpdateChannelNames
{
    public const string Stable = "stable";
    public const string Develop = "develop";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Develop, StringComparison.OrdinalIgnoreCase))
        {
            return Develop;
        }

        return Stable;
    }
}
