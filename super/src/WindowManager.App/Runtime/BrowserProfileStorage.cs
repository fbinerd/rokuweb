using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WindowManager.App.Runtime;

public static class BrowserProfileStorage
{
    public static string NormalizeName(string? profileName)
    {
        return profileName?.Trim() ?? string.Empty;
    }

    public static string EnsureProfileDirectory(string profileName)
    {
        var normalized = NormalizeName(profileName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("O nome do perfil de navegador nao pode ficar vazio.");
        }

        Directory.CreateDirectory(AppDataPaths.CefBrowserProfilesRoot);
        var profileDirectory = GetProfileDirectory(normalized);
        Directory.CreateDirectory(profileDirectory);
        return profileDirectory;
    }

    public static void DeleteProfileDirectory(string profileName)
    {
        var normalized = NormalizeName(profileName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var profileDirectory = GetProfileDirectory(normalized);
        if (Directory.Exists(profileDirectory))
        {
            Directory.Delete(profileDirectory, recursive: true);
        }
    }

    public static string GetProfileDirectory(string profileName)
    {
        var normalized = NormalizeName(profileName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var safeName = new string(normalized
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "perfil";
        }

        var hash = ComputeShortHash(normalized);
        return Path.Combine(AppDataPaths.CefBrowserProfilesRoot, string.Format("{0}-{1}", safeName, hash));
    }

    private static string ComputeShortHash(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes, 0, 6).Replace("-", string.Empty);
        }
    }
}
