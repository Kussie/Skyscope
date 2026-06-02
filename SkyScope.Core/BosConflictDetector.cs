using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class BosConflictDetector
{
    public BosConflictSummary DetectConflicts(List<BosSwapRule> rules, ModReferenceLibrary? library = null)
    {
        var map = new Dictionary<string, (BosObjectRef Ref, List<BosConflictSource> Sources)>();

        foreach (var rule in rules)
        {
            foreach (var orig in rule.OriginalObjects)
            {
                var baseKey = orig.NormalizedKey;
                if (string.IsNullOrEmpty(baseKey)) continue;

                // Include the conditional section in the grouping key so swaps gated on different
                // conditions (e.g. Fencewoven01 under WhiterunLocation vs FalkreathLocation) aren't
                // treated as competing — only rules under the same condition can actually clash.
                var conditionKey = rule.ConditionalSection?.ToLowerInvariant() ?? "";

                // Also factor in BOS's reference-filter properties (posA/posB/bnds/flags). A rule
                // scoped to a specific world reference targets a different set of objects than an
                // unfiltered base-form swap, so the two don't actually compete. Whitespace is
                // stripped so trivial formatting differences don't accidentally split a real group.
                var filterKey = StripWhitespace(rule.PropertiesFilter).ToLowerInvariant();
                var key       = $"{baseKey}##{conditionKey}##{filterKey}";

                if (!map.TryGetValue(key, out var entry))
                {
                    entry = (orig, new List<BosConflictSource>());
                    map[key] = entry;
                }

                entry.Sources.Add(new BosConflictSource
                {
                    FilePath           = rule.SourceFile,
                    LineNumber         = rule.LineNumber,
                    PrecedingLine      = rule.PrecedingLine,
                    LineText           = rule.LineText,
                    FollowingLine      = rule.FollowingLine,
                    SwapTarget         = rule.SwapTarget,
                    ConditionalSection = rule.ConditionalSection,
                    SectionType        = rule.SectionType,
                    Chance             = rule.Chance
                });
            }
        }

        List<BosConflictEntry> conflicts = [];

        foreach (var (_, (objRef, sources)) in map)
        {
            if (sources.Count < 2) continue;

            var first = sources[0].SwapTarget;
            if (sources.All(s => string.Equals(s.SwapTarget, first, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sorted = sources
                .OrderBy(s => Path.GetFileName(s.FilePath), StringComparer.Ordinal)
                .ToList();

            var entry = new BosConflictEntry { ObjectRef = objRef, Sources = sorted };

            // Enrich display name from the library when available
            if (library != null)
                entry.ResolvedName = library.GetDisplayName(objRef.NormalizedKey);

            conflicts.Add(entry);
        }

        conflicts.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return new BosConflictSummary { SwapConflicts = conflicts };
    }

    private static string StripWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) buf.Append(c);
        return buf.ToString();
    }
}
