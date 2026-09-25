using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class SpidConfigParser
{
    public (List<DistributionRule> Rules, string[] AllFiles, List<string> Errors, List<ProblemEntry> LineProblems, List<string> DynamicKeywords) LoadDistributionRulesFromDirectory(
        string dataPath, EditOutputOptions outputOptions = default)
    {
        if (!Directory.Exists(dataPath))
            return (new(), [], [], [], []);

        var files           = ConfigFiles.Enumerate(dataPath, "*_DISTR.ini", SpidLoadOrderComparer.Instance);
        var rules           = new List<DistributionRule>();
        var errors          = new List<string>();
        var lineProblems    = new List<ProblemEntry>();
        var dynamicKeywords = new List<string>();

        foreach (var filePath in files)
        {
            try { rules.AddRange(ParseFile(filePath, outputOptions, lineProblems, dynamicKeywords)); }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse {filePath}: {ex.Message}");
                Debug.WriteLine($"[SPID] Skipped {filePath}: {ex.Message}");
            }
        }

        return (rules, files, errors, lineProblems, dynamicKeywords);
    }

    private static string StripHexPrefix(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

    // Field 1 only — '+' is handled separately (checked before this runs), and Field 1 is the only
    // field that supports '*' (substring/ANY); Field 2 has no equivalent (see DetectFormModifier).
    private static (SpidFilterModifier modifier, string stripped) DetectModifier(string raw)
    {
        if (raw.Length == 0) return (SpidFilterModifier.Match, raw);
        return raw[0] switch
        {
            '-' => (SpidFilterModifier.Not,       raw[1..].Trim()),
            '*' => (SpidFilterModifier.Substring, raw[1..].Trim()),
            _   => (SpidFilterModifier.Match,     raw)
        };
    }

    // Field 2 only — no '*' support in the real engine (FormFiltersComponentParser only allows the
    // combine/exclusion modifiers, not partial-match), so a leading '*' here is literal text.
    private static (SpidFilterModifier modifier, string stripped) DetectFormModifier(string raw)
    {
        if (raw.Length == 0) return (SpidFilterModifier.Match, raw);
        return raw[0] == '-' ? (SpidFilterModifier.Not, raw[1..].Trim()) : (SpidFilterModifier.Match, raw);
    }

    // Only flags issues that don't depend on knowing SPID's full key vocabulary — many real SPID
    // keys (Keyword=, Item=, Shout=, Package=, ...) aren't modeled by this parser at all, and an
    // unrecognized key could be one of those rather than a typo, so unknown keys stay silent.
    private static IEnumerable<DistributionRule> ParseFile(
        string filePath, EditOutputOptions outputOptions = default, List<ProblemEntry>? lineProblems = null,
        List<string>? dynamicKeywords = null)
    {
        lineProblems    ??= [];
        dynamicKeywords ??= [];

        var readPath = EditOutputPathResolver.ResolveForRead(filePath, outputOptions);
        var lines = File.ReadAllLines(readPath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line    = lines[i];
            // TrimStart('﻿') guards against a stray BOM (e.g. from a pasted-in first line)
            // that .Trim() alone won't strip, since .NET doesn't treat it as whitespace.
            var trimmed = line.Trim().TrimStart('﻿');

            if (string.IsNullOrEmpty(trimmed) || trimmed[0] == ';' || trimmed.StartsWith("//")
                || trimmed[0] == '[') continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0)
            {
                lineProblems.Add(NewLineProblem(filePath, i + 1, line,
                    "Malformed line", "This line has no '=' and was ignored."));
                continue;
            }

            var keyLower = trimmed[..eqIdx].Trim().ToLowerInvariant();

            // Keyword= can create a brand-new keyword at runtime from a bare EditorID (not a
            // Plugin|FormId reference to an existing one) — not modeled as a DistributionRule, but
            // worth registering so other files' filters referencing it aren't flagged as unresolved.
            if (keyLower == "keyword")
            {
                var kwField0 = trimmed[(eqIdx + 1)..].Trim().Split('|')[0].Trim();
                if (!string.IsNullOrEmpty(kwField0) && !kwField0.Contains('|')
                    && !kwField0.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && !uint.TryParse(kwField0, out _))
                    dynamicKeywords.Add(kwField0);
                continue;
            }

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
            if (string.IsNullOrEmpty(value))
            {
                lineProblems.Add(NewLineProblem(filePath, i + 1, line,
                    "Empty value", $"'{trimmed[..eqIdx].Trim()}' has no value and was ignored."));
                continue;
            }

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
                    if (string.IsNullOrEmpty(f) || f.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

                    // Combined filter: every '+'-joined piece must match (AND). The real engine
                    // checks for '+' anywhere in the token, before any leading modifier, and stores
                    // each piece as its own plain string filter — no direct-ref extraction.
                    if (f.Contains('+'))
                    {
                        foreach (var piece in f.Split('+'))
                        {
                            var p = piece.Trim();
                            if (!string.IsNullOrEmpty(p))
                                stringFilters.Add(new SpidStringFilter { Modifier = SpidFilterModifier.All, Text = p });
                        }
                        continue;
                    }

                    var (mod, text) = DetectModifier(f);
                    if (string.IsNullOrEmpty(text) || text.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
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
                    if (string.IsNullOrEmpty(f) || f.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

                    // Combined filter: every '+'-joined piece must match (AND), each resolved the
                    // same way a standalone entry would be.
                    if (f.Contains('+'))
                    {
                        foreach (var piece in f.Split('+'))
                        {
                            var p = piece.Trim();
                            if (!string.IsNullOrEmpty(p) && !uint.TryParse(p, out _))
                                AddFormFilter(formFilters, SpidFilterModifier.All, p);
                        }
                        continue;
                    }

                    var (mod, text) = DetectFormModifier(f);
                    if (string.IsNullOrEmpty(text) || text.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (uint.TryParse(text, out _)) continue; // bare decimal — skip

                    AddFormFilter(formFilters, mod, text);
                }
            }

            // Skip rules with no targeting at all — worth flagging (a stripped Field 1 is a common
            // editing mistake) but not necessarily wrong, since matching every NPC can be intentional.
            if (npcRefs.Count == 0 && stringFilters.Count == 0 && formFilters.Count == 0)
            {
                lineProblems.Add(NewLineProblem(filePath, i + 1, line,
                    "No targeting", "This rule has no NPC, keyword, or faction filtering and will match every NPC."));
                continue;
            }

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

            // ── Field 6: Chance (and deterministic flag) ────────────────────────
            // Chance is a percentage and can be fractional (e.g. "12.5"), not just whole numbers.
            bool   isDeterministic = false;
            double chance          = 100;
            if (fields.Length > 6)
            {
                var chanceStr = fields[6].Trim();
                if (chanceStr.EndsWith('!'))
                {
                    isDeterministic = true;
                    chanceStr = chanceStr[..^1].Trim();
                }
                if (double.TryParse(chanceStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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

    // Resolves one Field-2 entry into a SpidFormFilter, matching the real engine's token
    // classification exactly (ClibUtil::distribution::get_record_type/get_record): '~' anywhere
    // wins first, then a bare mod filename (contains ".es"), then "0x"-prefixed hex with no plugin
    // context, else a plain EditorId. There is no "Plugin|FormId" pipe syntax here — that's
    // SkyPatcher's own convention; a token like "Some.esp|0x1234" would classify as a (broken) bare
    // mod-name lookup in real SPID, not a Plugin+FormId reference, so this doesn't special-case '|'.
    private static void AddFormFilter(List<SpidFormFilter> formFilters, SpidFilterModifier mod, string text)
    {
        var ti = text.IndexOf('~');
        if (ti > 0)
        {
            var fid = StripHexPrefix(text[..ti].Trim());
            var plg = text[(ti + 1)..].Trim();
            if (!string.IsNullOrEmpty(fid) && !string.IsNullOrEmpty(plg))
                formFilters.Add(new SpidFormFilter { Modifier = mod, Plugin = plg, FormId = fid });
            return;
        }

        // Bare plugin filename, no FormId — matches any NPC touched by this plugin, not a
        // specific record ("Only MyPlugin.esp" case in the real engine's form lookup).
        if (text.Contains(".es", StringComparison.OrdinalIgnoreCase))
        {
            formFilters.Add(new SpidFormFilter { Modifier = mod, Plugin = text });
            return;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Bare FormId, no plugin context (global lookup by raw FormID) — not an EditorId.
            formFilters.Add(new SpidFormFilter { Modifier = mod, FormId = StripHexPrefix(text) });
            return;
        }

        // Plain EditorId (faction, race, keyword, class, or any other record type)
        formFilters.Add(new SpidFormFilter { Modifier = mod, EditorId = text });
    }

    private static ProblemEntry NewLineProblem(
        string sourceFile, int lineNumber, string lineText, string category, string message) => new()
    {
        FilePath   = sourceFile,
        LineNumber = lineNumber,
        LineText   = lineText.Trim(),
        SourceTool = "SPID",
        Severity   = ProblemSeverity.Warning,
        Category   = category,
        Message    = message
    };

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
                // "-F" (not female) and "-M" (not male) are valid aliases in the real engine.
                case "M":
                case "-F": filter.Male       = true;  any = true; break;
                case "F":
                case "-M": filter.Male       = false; any = true; break;
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
