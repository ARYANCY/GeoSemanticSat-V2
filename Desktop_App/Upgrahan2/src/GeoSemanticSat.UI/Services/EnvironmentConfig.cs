using System;
using System.Collections.Generic;
using System.IO;

namespace GeoSemanticSat.UI.Services;

/// <summary>
/// Minimal reader for a .env file in the repository root.
///
/// Basemap API keys must not live in source. This looks for a .env beside the
/// executable and in each parent directory, so it works both from bin/ during
/// development and from a published folder. Absent file is not an error: every
/// default basemap provider is keyless.
/// </summary>
public static class EnvironmentConfig
{
    private static readonly Lazy<Dictionary<string, string>> Values = new(Load);

    public static string? Get(string key)
        => Values.Value.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(candidate))
                {
                    foreach (string raw in File.ReadAllLines(candidate))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        string k = line[..eq].Trim();
                        string v = line[(eq + 1)..].Trim().Trim('"');
                        result[k] = v;
                    }
                    break;
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // A malformed or unreadable .env must never stop the app starting.
        }
        return result;
    }
}
