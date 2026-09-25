using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SkyScope.Core;

public readonly record struct ModNameMatch(NpcFaceFinderFace Face, double Score);

// Fuzzy-matches a plugin filename against npcfacefinder.com's per-face mod names. There's no
// exact identifier shared between the two (the API never exposes a plugin filename), so this is
// inherently approximate — good enough to prefer over showing nothing, not a guarantee.
public static class ModNameMatcher
{
    // Below this, no candidate is treated as a match — an honest "couldn't tell" beats a
    // confident-looking wrong guess.
    private const double MinimumScore = 0.4;

    private static readonly Regex CamelBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumeric = new(@"[^a-z0-9\s]", RegexOptions.Compiled);
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "ae", "sse", "le", "esp", "esl", "esm", "v", "final", "patch", "fix"
    };

    public static string DeriveFriendlyName(string pluginFileName)
    {
        if (string.IsNullOrWhiteSpace(pluginFileName)) return "";

        var name = System.IO.Path.GetFileNameWithoutExtension(pluginFileName);
        name = CamelBoundary.Replace(name, " ");
        name = name.Replace('_', ' ').Replace('-', ' ');
        return name.Trim();
    }

    public static ModNameMatch? FindBestMatch(string pluginFileName, IEnumerable<NpcFaceFinderFace> faces)
    {
        var friendly = DeriveFriendlyName(pluginFileName);
        if (string.IsNullOrEmpty(friendly)) return null;

        var target = Tokenize(friendly);
        if (target.Count == 0) return null;

        ModNameMatch? best = null;
        foreach (var face in faces)
        {
            var score = Similarity(target, Tokenize(face.ModName));
            if (best is null || score > best.Value.Score)
                best = new ModNameMatch(face, score);
        }

        return best is { Score: >= MinimumScore } ? best : null;
    }

    // Orders arbitrary items by fuzzy similarity to a plugin filename, best first — used by the
    // manual match picker so likely candidates surface before the user types anything, rather than
    // requiring an exact substring match.
    public static List<T> RankBySimilarity<T>(string pluginFileName, IEnumerable<T> items, Func<T, string> nameSelector)
    {
        var target = Tokenize(DeriveFriendlyName(pluginFileName));
        return items
            .Select(item => (item, score: Similarity(target, Tokenize(nameSelector(item)))))
            .OrderByDescending(x => x.score)
            .Select(x => x.item)
            .ToList();
    }

    // Jaccard-style token overlap: shared significant words over the union of both sides.
    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Intersect(b).Count();
        var union  = a.Union(b).Count();
        return union == 0 ? 0 : (double)shared / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var normalized = NonAlphaNumeric.Replace(text.ToLowerInvariant(), " ");
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !NoiseTokens.Contains(t) && !IsVersionToken(t));
        return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    // "v2", "2", "1_0" style version fragments — not meaningful for name similarity.
    private static bool IsVersionToken(string token) =>
        token.All(char.IsDigit) || (token.Length > 1 && token[0] == 'v' && token[1..].All(char.IsDigit));
}
