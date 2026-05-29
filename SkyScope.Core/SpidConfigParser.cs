using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class SpidConfigParser
{
    public (List<DistributionRule> Rules, int FilesScanned, List<string> Errors) LoadDistributionRulesFromDirectory(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return (new(), 0, []);

        var files = ConfigFiles.Enumerate(dataPath, "*_DISTR.ini");
        var rules  = new List<DistributionRule>();
        var errors = new List<string>();

        foreach (var filePath in files)
        {
            try { rules.AddRange(ParseFile(filePath)); }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse {filePath}: {ex.Message}");
                Debug.WriteLine($"[SPID] Skipped {filePath}: {ex.Message}");
            }
        }

        return (rules, files.Length, errors);
    }

    private static string StripHexPrefix(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

    private static (SpidFilterModifier modifier, string stripped) DetectModifier(string raw)
    {
        if (raw.Length == 0) return (SpidFilterModifier.Match, raw);
        return raw[0] switch
        {
            '-' => (SpidFilterModifier.Not,       raw[1..].Trim()),
            '+' => (SpidFilterModifier.All,       raw[1..].Trim()),
            '*' => (SpidFilterModifier.Substring, raw[1..].Trim()),
            _   => (SpidFilterModifier.Match,     raw)
        };
    }

    private static IEnumerable<DistributionRule> ParseFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line    = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed[0] == ';') continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;

            var keyLower = trimmed[..eqIdx].Trim().ToLowerInvariant();
            var ruleType = keyLower switch
            {
                "outfit" or "finaloutfit" or "sleepoutfit" => (RuleType?)RuleType.OutfitDefault,
                "spell"                                    => RuleType.Spell,
                "perk"                                     => RuleType.Perk,
                _                                          => null
            };
            if (ruleType is null) continue;

            bool isFinalOutfit = keyLower == "finaloutfit";

            var value = trimmed[(eqIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(value)) continue;

            // Format: RuleValue | StringFilters | FormFilters | LevelFilters | TraitFilters | Count | Chance
            var fields    = value.Split('|');
            var ruleValue = fields[0].Trim();

            var npcRefs         = new List<NpcReference>();
            var stringFilters   = new List<SpidStringFilter>();
            var formFilters     = new List<SpidFormFilter>();
            string? levelFilter = null;
            SpidTraitFilter? traitFilter = null;

            // ── Field 1: StringFilters ─────────────────────────────────────────
            // 0x~Plugin refs = direct NPC FormId → TargetNpcs + SpidStringFilters
            // Plain text (names, keywords, EditorIds) → SpidStringFilters only
            if (fields.Length > 1)
            {
                foreach (var raw in fields[1].Split(','))
                {
                    var f = raw.Trim();
                    if (string.IsNullOrEmpty(f)) continue;

                    var (mod, text) = DetectModifier(f);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (uint.TryParse(text, out _)) continue; // bare decimal — skip

                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        var ti = text.IndexOf('~');
                        if (ti > 0)
                        {
                            var fid = StripHexPrefix(text[..ti].Trim());
                            var plg = text[(ti + 1)..].Trim();
                            if (!string.IsNullOrEmpty(fid) && !string.IsNullOrEmpty(plg))
                            {
                                stringFilters.Add(new SpidStringFilter { Modifier = mod, Text = text, Plugin = plg, FormId = fid });
                                if (mod != SpidFilterModifier.Not)
                                    npcRefs.Add(new NpcReference { RefType = NpcRefType.RecordId, Plugin = plg, FormId = fid });
                            }
                        }
                        else
                        {
                            // Bare 0x — local FormId ref, no plugin context
                            stringFilters.Add(new SpidStringFilter { Modifier = mod, Text = text });
                            if (mod != SpidFilterModifier.Not)
                                npcRefs.Add(new NpcReference { RefType = NpcRefType.LocalFormId, Identifier = text });
                        }
                        continue;
                    }

                    if (text.Contains('~') || text.Contains('|')) continue;

                    // Plain text (keyword name, display name, or EditorId) — filter only, not a direct NPC ref
                    stringFilters.Add(new SpidStringFilter { Modifier = mod, Text = text });
                }
            }

            // ── Field 2: FormFilters ───────────────────────────────────────────
            // These are faction/race/keyword/class FormIds — NOT direct NPC refs.
            // Only populate SpidFormFilters, never TargetNpcs.
            if (fields.Length > 2)
            {
                foreach (var raw in fields[2].Split(','))
                {
                    var f = raw.Trim();
                    if (string.IsNullOrEmpty(f)) continue;

                    var (mod, text) = DetectModifier(f);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (uint.TryParse(text, out _)) continue; // bare decimal — skip

                    // Plugin.esp|FormId or Plugin.esp|0xFormId
                    var pi = text.IndexOf('|');
                    if (pi > 0)
                    {
                        var plg = text[..pi].Trim();
                        var fid = StripHexPrefix(text[(pi + 1)..].Trim());
                        if (!string.IsNullOrEmpty(plg) && !string.IsNullOrEmpty(fid))
                            formFilters.Add(new SpidFormFilter { Modifier = mod, Plugin = plg, FormId = fid });
                        continue;
                    }

                    // 0x~Plugin or bare 0x
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        var ti = text.IndexOf('~');
                        if (ti > 0)
                        {
                            var fid = StripHexPrefix(text[..ti].Trim());
                            var plg = text[(ti + 1)..].Trim();
                            if (!string.IsNullOrEmpty(fid) && !string.IsNullOrEmpty(plg))
                                formFilters.Add(new SpidFormFilter { Modifier = mod, Plugin = plg, FormId = fid });
                        }
                        else
                        {
                            formFilters.Add(new SpidFormFilter { Modifier = mod, EditorId = text });
                        }
                        continue;
                    }

                    // Plain EditorId (faction, race, keyword, class EditorId)
                    formFilters.Add(new SpidFormFilter { Modifier = mod, EditorId = text });
                }
            }

            // Skip rules with no targeting at all
            if (npcRefs.Count == 0 && stringFilters.Count == 0 && formFilters.Count == 0) continue;

            // ── Field 3: LevelFilters ──────────────────────────────────────────
            if (fields.Length > 3)
            {
                var lf = fields[3].Trim();
                if (!string.IsNullOrEmpty(lf) && !lf.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    levelFilter = lf;
            }

            // ── Field 4: TraitFilters ──────────────────────────────────────────
            if (fields.Length > 4)
                traitFilter = ParseTraitFilter(fields[4].Trim());

            // ── Field 6: Chance (and deterministic flag) ───────────────────────
            bool isDeterministic = false;
            int  chance          = 100;
            if (fields.Length > 6)
            {
                var chanceStr = fields[6].Trim();
                if (chanceStr.EndsWith('!'))
                {
                    isDeterministic = true;
                    chanceStr = chanceStr[..^1].Trim();
                }
                if (int.TryParse(chanceStr, out var parsed))
                    chance = parsed;
            }

            string? preceding = i > 0               ? lines[i - 1] : null;
            string? following = i < lines.Length - 1 ? lines[i + 1] : null;

            yield return new DistributionRule
            {
                TargetNpcs        = npcRefs,
                SpidStringFilters = stringFilters,
                SpidFormFilters   = formFilters,
                SpidTraitFilter   = traitFilter,
                SpidLevelFilter   = levelFilter,
                IsFinalOutfit     = isFinalOutfit,
                IsDeterministic   = isDeterministic,
                RuleType          = ruleType.Value,
                RuleValue         = ruleValue,
                SourceFile        = filePath,
                LineNumber        = i + 1,
                PrecedingLine     = preceding,
                LineText          = line,
                FollowingLine     = following,
                SourceTool        = "SPID",
                SpidChance        = chance
            };
        }
    }

    private static SpidTraitFilter? ParseTraitFilter(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        var filter = new SpidTraitFilter();
        bool any   = false;

        foreach (var token in raw.Split('/'))
        {
            switch (token.Trim().ToUpperInvariant())
            {
                case "M":  filter.Male       = true;  any = true; break;
                case "F":  filter.Male       = false; any = true; break;
                case "U":  filter.Unique     = true;  any = true; break;
                case "-U": filter.Unique     = false; any = true; break;
                case "C":  filter.Child      = true;  any = true; break;
                case "-C": filter.Child      = false; any = true; break;
                case "S":  filter.Summonable = true;  any = true; break;
                case "-S": filter.Summonable = false; any = true; break;
                case "L":  filter.Leveled    = true;  any = true; break;
                case "-L": filter.Leveled    = false; any = true; break;
                case "T":  filter.Teammate   = true;  any = true; break;
                case "-T": filter.Teammate   = false; any = true; break;
                case "D":  filter.Dead       = true;  any = true; break;
                case "-D": filter.Dead       = false; any = true; break;
            }
        }

        return any ? filter : null;
    }
}
