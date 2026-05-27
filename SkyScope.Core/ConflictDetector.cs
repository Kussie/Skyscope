using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class ConflictDetector
{
    public ConflictSummary DetectConflicts(List<ModConfiguration> configurations, NpcNameDatabase? db = null)
    {
        // Maps: normalizedNpcKey -> (representative NpcReference, list of conflict sources)
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
                    var key = ResolveKey(npcRef, db);
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
        // Spell/perk are additive — same value from two sources is still a real duplicate
        BuildConflictsAdditive(spellMap, summary.SpellConflicts);
        BuildConflictsAdditive(perkMap,  summary.PerkConflicts);

        return summary;
    }

    // Resolve any reference type to a canonical "EID:..." key when the NPC database is available,
    // so that FormId, EditorId, and Name references to the same NPC share a single map entry.
    private static string ResolveKey(NpcReference npcRef, NpcNameDatabase? db)
    {
        if (db != null)
        {
            if (npcRef.RefType == NpcRefType.RecordId)
            {
                var eid = db.ResolveEditorId(npcRef.Plugin, npcRef.FormId);
                if (!string.IsNullOrEmpty(eid))
                    return $"EID:{eid.ToLowerInvariant()}";
                // RecordId resolves to nothing — NPC not in DB, skip it
                return string.Empty;
            }
            else if (npcRef.RefType == NpcRefType.Name)
            {
                var eid = db.FindEditorIdByName(npcRef.Identifier);
                if (!string.IsNullOrEmpty(eid))
                    return $"EID:{eid.ToLowerInvariant()}";
                // Value may itself be an NPC EditorId (SPID allows both forms in StringFilters)
                if (db.IsNpcEditorId(npcRef.Identifier))
                    return $"EID:{npcRef.Identifier.ToLowerInvariant()}";
                // Cannot be tied to a real NPC (e.g. ActorTypeNPC keyword, unresolvable hex) — skip
                return string.Empty;
            }
            else if (npcRef.RefType == NpcRefType.EditorId)
            {
                // Validate the EditorId belongs to an actual NPC, not a keyword/faction/etc.
                if (!db.IsNpcEditorId(npcRef.Identifier))
                    return string.Empty;
            }
            else if (npcRef.RefType == NpcRefType.LocalFormId)
            {
                // Bare hex form ID with no plugin context — search all loaded plugins.
                // Only accepted when exactly one NPC in the load order has this local form ID.
                var eid = db.ResolveEditorIdByLocalFormId(npcRef.Identifier);
                if (!string.IsNullOrEmpty(eid))
                    return $"EID:{eid.ToLowerInvariant()}";
                return string.Empty; // not a unique NPC match → skip
            }
        }
        return npcRef.NormalizedKey;
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

    // Additive rules (Spell/Perk): ALL sources apply, so even same-value entries from
    // different files represent duplicate distribution and are worth surfacing.
    private static void BuildConflictsAdditive(
        Dictionary<string, (NpcReference Ref, List<ConflictSource> Sources)> map,
        List<ConflictEntry> target)
    {
        foreach (var (_, (npcRef, sources)) in map)
        {
            if (sources.Count < 2) continue;

            target.Add(new ConflictEntry
            {
                NpcRef  = npcRef,
                Sources = new List<ConflictSource>(sources)
            });
        }
    }
}
