using System.Collections.Generic;
using WindowManager.App.Profiles;
using WindowManager.App.Runtime;

namespace WindowManager.App.ViewModels;

internal sealed class BrowserProfileDefinitionNameComparer : IEqualityComparer<BrowserProfileDefinition>
{
    public bool Equals(BrowserProfileDefinition? x, BrowserProfileDefinition? y)
    {
        return string.Equals(
            BrowserProfileStorage.NormalizeName(x?.Name),
            BrowserProfileStorage.NormalizeName(y?.Name),
            System.StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(BrowserProfileDefinition obj)
    {
        return (BrowserProfileStorage.NormalizeName(obj.Name)).ToUpperInvariant().GetHashCode();
    }
}
