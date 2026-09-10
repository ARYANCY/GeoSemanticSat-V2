using System.Text.RegularExpressions;

namespace GeoSemanticSat.Tests;

/// <summary>
/// The UI resolves brushes by string key (<c>Themed("AccentBrush")</c>). A typo
/// or a renamed token does not fail the build and does not throw at runtime - it
/// silently yields a transparent brush, so badges and status markers just vanish.
/// This pins every key the code-behind asks for to a key the token file defines.
/// </summary>
public class ThemeTokenTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GeoSemanticSat.UI")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "Could not locate the src directory from the test output folder.");
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void EveryBrushKeyRequestedByCodeBehindExistsInTokens()
    {
        var tokens = File.ReadAllText(RepoFile("GeoSemanticSat.UI", "Resources", "Tokens.axaml"));
        var codeBehind = File.ReadAllText(RepoFile("GeoSemanticSat.UI", "MainWindow.axaml.cs"));

        var defined = Regex.Matches(tokens, @"x:Key=""([^""]+)""")
                           .Select(m => m.Groups[1].Value)
                           .ToHashSet(StringComparer.Ordinal);

        var requested = Regex.Matches(codeBehind, @"Themed\(\s*""([^""]+)""")
                             .Select(m => m.Groups[1].Value)
                             .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(requested);

        var missing = requested.Where(k => !defined.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Tokens.axaml is missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryChangeTypeMapsToADefinedBadgeBrush()
    {
        var tokens = File.ReadAllText(RepoFile("GeoSemanticSat.UI", "Resources", "Tokens.axaml"));

        foreach (var key in new[]
                 {
                     "TypeConstructionBrush", "TypeClearanceBrush", "TypeWaterBrush",
                     "TypeRoadBrush", "TypeActivityBrush", "TypeNeutralBrush",
                 })
        {
            Assert.Contains($"x:Key=\"{key}\"", tokens);
        }
    }
}
