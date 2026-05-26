using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class ConflictDetector
{
    public ConflictSummary DetectConflicts(List<ModConfiguration> configurations)
    {
        // Maps: normalizedNpcKey -> (representative NpcReference, list of conflict sources)
        var appearanceMap = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var skinMap       = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var outfitMap     = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();

        foreach (var config in configurations)
        {
            foreach (var rule in config.Rules)
            {
                var map = rule.RuleType switch
                {
                    RuleType.Appearance    => appearanceMap,
                    RuleType.Skin          => skinMap,
                    RuleType.OutfitDefault => outfitMap,
                    _                      => null
                };

                if (map is null) continue;

                foreach (var npcRef in rule.TargetNpcs)
                {
                    var key = npcRef.NormalizedKey;
                    if (string.IsNullOrEmpty(key)) continue;

                    if (!map.TryGetValue(key, out var entry))
                    {
                        entry = (npcRef, new List<ConflictSource>());
                        map[key] = entry;
                    }

                    entry.Sources.Add(new ConflictSource
                    {
                        FilePath      = rule.SourceFile,
                        LineNumber    = rule.LineNumber,
                        PrecedingLine = rule.PrecedingLine,
                        ConflictLine  = rule.LineText,
                        FollowingLine = rule.FollowingLine,
                        RuleValue     = rule.RuleValue
                    });
                }
            }
        }

        var summary = new ConflictSummary { TotalFilesScanned = configurations.Count };

        BuildConflicts(appearanceMap, summary.AppearanceConflicts);
        BuildConflicts(skinMap,       summary.SkinConflicts);
        BuildConflicts(outfitMap,     summary.OutfitDefaultConflicts);

        return summary;
    }

    private static void BuildConflicts(
        Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)> map,
        List<ConflictEntry> target)
    {
        foreach (var (_, (npcRef, sources)) in map)
        {
            if (sources.Count < 2) continue;

            // All sources set the same value — redundant but not a true conflict
            var first = sources[0].RuleValue;
            if (sources.All(s => string.Equals(s.RuleValue, first, StringComparison.OrdinalIgnoreCase)))
                continue;

            target.Add(new ConflictEntry
            {
                NpcRef  = npcRef,
                Sources = new List<ConflictSource>(sources)
            });
        }
    }
}
