using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class ConflictDetector
{
    public ConflictSummary DetectConflicts(List<ModConfiguration> configurations, ModReferenceLibrary? library = null)
    {
        var appearanceMap = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var skinMap       = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var outfitMap     = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var spellMap      = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();
        var perkMap       = new Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)>();

        foreach (var config in configurations)
        {
            foreach (var rule in config.Rules)
            {
                var map = rule.RuleType switch
                {
                    RuleType.Appearance    => appearanceMap,
                    RuleType.Skin          => skinMap,
                    RuleType.OutfitDefault => outfitMap,
                    RuleType.Spell         => spellMap,
                    RuleType.Perk          => perkMap,
                    _                      => null
                };

                if (map is null) continue;

                foreach (var npcRef in rule.TargetNpcs)
                {
                    var key = ResolveKey(npcRef, library);
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

        // TotalFilesScanned is set by the caller from the actual on-disk scan count.
        var summary = new ConflictSummary();

        BuildConflicts(appearanceMap, summary.AppearanceConflicts);
        BuildConflicts(skinMap,       summary.SkinConflicts);
        BuildConflicts(outfitMap,     summary.OutfitDefaultConflicts);
        BuildConflicts(spellMap, summary.SpellConflicts, additive: true);
        BuildConflicts(perkMap,  summary.PerkConflicts,  additive: true);

        return summary;
    }

    // Falls back to the raw NormalizedKey when the library can't canonicalise the reference —
    // otherwise SkyPatcher rules targeting custom-mod NPCs (whose EditorIds the library doesn't
    // know) are silently dropped and their conflicts never surface.
    private static string ResolveKey(NpcReference npcRef, ModReferenceLibrary? library)
    {
        if (library == null) return npcRef.NormalizedKey;
        var key = library.GetCanonicalKey(npcRef);
        return !string.IsNullOrEmpty(key) ? key : npcRef.NormalizedKey;
    }

    // When additive=true (Spell/Perk), same-value entries from different files are still duplicates.
    private static void BuildConflicts(
        Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)> map,
        List<ConflictEntry> target,
        bool additive = false)
    {
        foreach (var (_, (npcRef, sources)) in map)
        {
            if (sources.Count < 2) continue;

            if (!additive)
            {
                var first = sources[0].RuleValue;
                if (sources.All(s => string.Equals(s.RuleValue, first, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            target.Add(new ConflictEntry
            {
                NpcRef  = npcRef,
                Sources = new List<ConflictSource>(sources)
            });
        }
    }
}
