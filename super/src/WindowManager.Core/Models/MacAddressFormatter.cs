using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowManager.Core.Models;

public static class MacAddressFormatter
{
    public static string Normalize(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12)
        {
            return raw.ToUpperInvariant();
        }

        return string.Join("-", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    public static List<string> NormalizeMany(IEnumerable<string>? values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
