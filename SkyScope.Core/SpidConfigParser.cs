using System;
using System.Collections.Generic;
using System.IO;
using SkyScope.Models;

namespace SkyScope.Core;

public class SpidConfigParser
{
    // Returns all Outfit/Spell/Perk rules found across all *_DISTR.ini files under dataPath.
    public (List<SkyPatcherRule> Rules, int FilesScanned) LoadDistributionRulesFromDirectory(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return (new(), 0);

        var files = Directory.GetFiles(dataPath, "*_DISTR.ini", SearchOption.AllDirectories);
        var rules = new List<SkyPatcherRule>();

        foreach (var filePath in files)
        {
            try { rules.AddRange(ParseFile(filePath)); }
            catch { }
        }

        return (rules, files.Length);
    }

    private static IEnumerable<SkyPatcherRule> ParseFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line    = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed[0] == ';') continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;

            var keyName = trimmed[..eqIdx].Trim();
            var ruleType = keyName.ToLowerInvariant() switch
            {
                "outfit" => (RuleType?)RuleType.OutfitDefault,
                "spell"  => RuleType.Spell,
                "perk"   => RuleType.Perk,
                _        => null
            };
            if (ruleType is null) continue;

            var value = trimmed[(eqIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(value)) continue;

            // Format: outfit | StringFilters | FormFilters | LevelFilters | TraitFilters | Count | Chance
            var fields    = value.Split('|');
            var ruleValue = fields[0].Trim();
            var npcRefs   = new List<NpcReference>();

            // Field 1 — StringFilters: SPID matches these against the NPC's display name.
            // Comma-separated; values without spaces are usually EditorIds used as display names,
            // values with spaces are full names. Both get NpcRefType.Name so they go through the
            // name→EditorId reverse lookup during merge.
            if (fields.Length > 1)
            {
                foreach (var raw in fields[1].Split(','))
                {
                    var f = raw.Trim();
                    if (string.IsNullOrEmpty(f) || f[0] == '-') continue;

                    npcRefs.Add(new NpcReference
                    {
                        RefType    = NpcRefType.Name,
                        Identifier = f
                    });
                }
            }

            // Field 2 — FormFilters: explicit EditorId (or plugin|formid) references.
            // We only handle plain EditorIds here; plugin|formid and 0x hex values are skipped.
            if (fields.Length > 2)
            {
                foreach (var raw in fields[2].Split(','))
                {
                    var f = raw.Trim();
                    if (string.IsNullOrEmpty(f) || f[0] == '-') continue;
                    if (f.Contains('|')) continue;                               // plugin|formid
                    if (f.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue; // hex formid
                    if (uint.TryParse(f, out _)) continue;                       // decimal formid

                    npcRefs.Add(new NpcReference
                    {
                        RefType    = NpcRefType.EditorId,
                        Identifier = f
                    });
                }
            }

            if (npcRefs.Count == 0) continue;

            // Field 6 — Chance (0-100). Absent or "NONE" means 100.
            int chance = 100;
            if (fields.Length > 6)
            {
                var chanceStr = fields[6].Trim();
                if (int.TryParse(chanceStr, out var parsed))
                    chance = parsed;
            }

            string? preceding = i > 0              ? lines[i - 1] : null;
            string? following = i < lines.Length - 1 ? lines[i + 1] : null;

            yield return new SkyPatcherRule
            {
                TargetNpcs    = npcRefs,
                RuleType      = ruleType.Value,
                RuleValue     = ruleValue,
                SourceFile    = filePath,
                LineNumber    = i + 1,
                PrecedingLine = preceding,
                LineText      = line,
                FollowingLine = following,
                SourceTool    = "SPID",
                SpidChance    = chance
            };
        }
    }
}
