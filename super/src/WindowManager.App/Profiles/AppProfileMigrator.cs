using System;
using System.Collections.Generic;
using System.Linq;
using WindowManager.App.Runtime;

namespace WindowManager.App.Profiles;

public static class AppProfileMigrator
{
    public static bool Migrate(AppProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var changed = EnsureCollections(profile);
        var schemaVersion = profile.SchemaVersion <= 0
            ? AppProfileSchema.InitialVersion
            : profile.SchemaVersion;

        while (schemaVersion < AppProfileSchema.CurrentVersion)
        {
            switch (schemaVersion)
            {
                case AppProfileSchema.InitialVersion:
                    changed |= ApplyBrowserProfilePatch(profile);
                    schemaVersion = AppProfileSchema.BrowserProfilesVersion;
                    break;
                default:
                    throw new InvalidOperationException(string.Format("Schema de base nao suportado: {0}", schemaVersion));
            }
        }

        changed |= NormalizeCurrentSchema(profile);

        if (profile.SchemaVersion != AppProfileSchema.CurrentVersion)
        {
            profile.SchemaVersion = AppProfileSchema.CurrentVersion;
            changed = true;
        }

        return changed;
    }

    private static bool EnsureCollections(AppProfile profile)
    {
        var changed = false;

        if (profile.Windows is null)
        {
            profile.Windows = new List<WindowSessionProfile>();
            changed = true;
        }

        if (profile.StaticPanels is null)
        {
            profile.StaticPanels = new List<StaticPanelProfile>();
            changed = true;
        }

        if (profile.DisplayTargets is null)
        {
            profile.DisplayTargets = new List<DisplayTargetProfile>();
            changed = true;
        }

        if (profile.DisplayBindings is null)
        {
            profile.DisplayBindings = new List<DisplayBindingProfile>();
            changed = true;
        }

        if (profile.TvProfiles is null)
        {
            profile.TvProfiles = new List<TvProfileDefinition>();
            changed = true;
        }

        if (profile.WindowProfiles is null)
        {
            profile.WindowProfiles = new List<WindowGroupProfile>();
            changed = true;
        }

        if (profile.ActiveSessions is null)
        {
            profile.ActiveSessions = new List<ActiveSessionRecord>();
            changed = true;
        }

        if (profile.BrowserProfiles is null)
        {
            profile.BrowserProfiles = new List<BrowserProfileDefinition>();
            changed = true;
        }

        foreach (var tvProfile in profile.TvProfiles)
        {
            if (tvProfile.Targets is null)
            {
                tvProfile.Targets = new List<TvProfileTargetDefinition>();
                changed = true;
            }
        }

        foreach (var windowProfile in profile.WindowProfiles)
        {
            if (windowProfile.Windows is null)
            {
                windowProfile.Windows = new List<WindowLinkProfile>();
                changed = true;
            }
        }

        foreach (var activeSession in profile.ActiveSessions)
        {
            if (activeSession.Windows is null)
            {
                activeSession.Windows = new List<ActiveSessionWindowRecord>();
                changed = true;
            }

            if (activeSession.BoundDisplays is null)
            {
                activeSession.BoundDisplays = new List<ActiveSessionDisplayBindingRecord>();
                changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyBrowserProfilePatch(AppProfile profile)
    {
        return NormalizeBrowserProfileAssignments(profile);
    }

    private static bool NormalizeCurrentSchema(AppProfile profile)
    {
        return NormalizeBrowserProfileAssignments(profile);
    }

    private static bool NormalizeBrowserProfileAssignments(AppProfile profile)
    {
        var changed = false;
        var knownProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var browserProfile in profile.BrowserProfiles.ToList())
        {
            var normalizedName = BrowserProfileStorage.NormalizeName(browserProfile.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                profile.BrowserProfiles.Remove(browserProfile);
                changed = true;
                continue;
            }

            if (!string.Equals(browserProfile.Name, normalizedName, StringComparison.Ordinal))
            {
                browserProfile.Name = normalizedName;
                changed = true;
            }

            if (!knownProfileNames.Add(normalizedName))
            {
                profile.BrowserProfiles.Remove(browserProfile);
                changed = true;
            }
        }

        foreach (var windowProfile in profile.WindowProfiles)
        {
            var normalizedAssignedName = BrowserProfileStorage.NormalizeName(windowProfile.BrowserProfileName);
            if (string.IsNullOrWhiteSpace(normalizedAssignedName))
            {
                normalizedAssignedName = GenerateUniqueBrowserProfileName(windowProfile.Name, knownProfileNames);
                windowProfile.BrowserProfileName = normalizedAssignedName;
                changed = true;
            }
            else if (!string.Equals(windowProfile.BrowserProfileName, normalizedAssignedName, StringComparison.Ordinal))
            {
                windowProfile.BrowserProfileName = normalizedAssignedName;
                changed = true;
            }

            if (knownProfileNames.Add(normalizedAssignedName))
            {
                profile.BrowserProfiles.Add(new BrowserProfileDefinition
                {
                    Name = normalizedAssignedName
                });
                changed = true;
            }

            foreach (var persistedWindow in profile.Windows.Where(x =>
                         x.ActiveSessionId == windowProfile.Id ||
                         string.Equals(x.ActiveSessionName, windowProfile.Name, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(x.ProfileName, windowProfile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(persistedWindow.BrowserProfileName, normalizedAssignedName, StringComparison.Ordinal))
                {
                    persistedWindow.BrowserProfileName = normalizedAssignedName;
                    changed = true;
                }
            }

            foreach (var activeSessionWindow in profile.ActiveSessions
                         .Where(x => x.Id == windowProfile.Id ||
                                     string.Equals(x.ProfileName, windowProfile.Name, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(x.Name, windowProfile.Name, StringComparison.OrdinalIgnoreCase))
                         .SelectMany(x => x.Windows))
            {
                if (!string.Equals(activeSessionWindow.BrowserProfileName, normalizedAssignedName, StringComparison.Ordinal))
                {
                    activeSessionWindow.BrowserProfileName = normalizedAssignedName;
                    changed = true;
                }
            }
        }

        foreach (var browserProfileName in knownProfileNames)
        {
            BrowserProfileStorage.EnsureProfileDirectory(browserProfileName);
        }

        var normalizedProfiles = profile.BrowserProfiles
            .Select(x => new BrowserProfileDefinition
            {
                Name = BrowserProfileStorage.NormalizeName(x.Name)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Distinct(BrowserProfileNameComparer.Instance)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!HaveSameNames(profile.BrowserProfiles, normalizedProfiles))
        {
            profile.BrowserProfiles = normalizedProfiles;
            changed = true;
        }

        return changed;
    }

    private static bool HaveSameNames(IReadOnlyList<BrowserProfileDefinition> current, IReadOnlyList<BrowserProfileDefinition> normalized)
    {
        if (current.Count != normalized.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!string.Equals(current[index].Name, normalized[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string GenerateUniqueBrowserProfileName(string? streamName, ISet<string> knownNames)
    {
        var baseName = BrowserProfileStorage.NormalizeName(streamName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Perfil navegador";
        }

        var candidate = baseName;
        var suffix = 2;
        while (knownNames.Contains(candidate))
        {
            candidate = string.Format("{0} {1}", baseName, suffix);
            suffix++;
        }

        return candidate;
    }

    private sealed class BrowserProfileNameComparer : IEqualityComparer<BrowserProfileDefinition>
    {
        public static BrowserProfileNameComparer Instance { get; } = new BrowserProfileNameComparer();

        public bool Equals(BrowserProfileDefinition? x, BrowserProfileDefinition? y)
        {
            return string.Equals(
                BrowserProfileStorage.NormalizeName(x?.Name),
                BrowserProfileStorage.NormalizeName(y?.Name),
                StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(BrowserProfileDefinition obj)
        {
            return BrowserProfileStorage.NormalizeName(obj.Name).ToUpperInvariant().GetHashCode();
        }
    }
}
