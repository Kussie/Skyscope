using System;
using System.Collections.Generic;
using System.Globalization;
using SkyScope.Models;

namespace SkyScope.Core;

// Expands filter-based SPID rules (Field 1 keyword/name entries, Field 2 FormFilters,
// Field 4 trait filters) to the set of NPC EditorIds they target.
// Rules that only contain direct 0x~Plugin NPC refs are handled by the existing direct-ref
// path in MergeSpidConflicts; this evaluator handles the rest.
public class SpidFilterEvaluator
{
    private readonly ModReferenceLibrary _library;

    public SpidFilterEvaluator(ModReferenceLibrary library)
    {
        _library = library;
    }

    // Returns the EditorIds (lowercase) of all library NPCs that match the rule's filter
    // criteria. Returns an empty list for pure direct-ref rules (nothing filter-based to expand).
    public List<string> ExpandFilterTargets(DistributionRule rule)
    {
        bool hasStringFilters = HasFilterEntries(rule.SpidStringFilters);
        bool hasFormFilters   = rule.SpidFormFilters.Count > 0;
        bool hasTraitFilter   = rule.SpidTraitFilter != null;

        if (!hasStringFilters && !hasFormFilters && !hasTraitFilter)
            return [];

        var results = new List<string>();

        foreach (var npc in _library.GetAllNpcs())
        {
            if (string.IsNullOrEmpty(npc.ResolvedEditorId)) continue;

            if (MatchesRule(npc, rule))
                results.Add(npc.ResolvedEditorId.ToLowerInvariant());
        }

        return results;
    }

    private bool MatchesRule(RecordInfo npc, DistributionRule rule)
    {
        // Trait filter — evaluated first as it is cheapest
        if (rule.SpidTraitFilter != null && !MatchesTrait(npc, rule.SpidTraitFilter))
            return false;

        // StringFilters: any Match/All/Substring entry must match; Not entries must NOT match.
        // At least one positive (non-Not) filter must be satisfied when positive filters exist.
        if (HasFilterEntries(rule.SpidStringFilters))
        {
            if (!MatchesStringFilters(npc, rule.SpidStringFilters))
                return false;
        }

        // FormFilters: attribute membership checks (factions, race, class, keywords).
        if (rule.SpidFormFilters.Count > 0)
        {
            if (!MatchesFormFilters(npc, rule.SpidFormFilters))
                return false;
        }

        return true;
    }

    // ── StringFilter evaluation ───────────────────────────────────────────────

    private bool MatchesStringFilters(RecordInfo npc, List<SpidStringFilter> filters)
    {
        // Skip entries that are direct FormId refs (those are handled by TargetNpcs).
        bool hasPositive = false;
        bool positive    = false;

        foreach (var f in filters)
        {
            if (f.Plugin != null) continue; // FormId ref — skip in filter evaluation

            bool matched = MatchesStringEntry(npc, f);

            switch (f.Modifier)
            {
                case SpidFilterModifier.Not:
                    if (matched) return false; // exclusion
                    break;
                case SpidFilterModifier.All:
                    hasPositive = true;
                    if (!matched) return false; // must match ALL entries
                    positive = true;
                    break;
                default: // Match / Substring
                    hasPositive = true;
                    if (matched) positive = true;
                    break;
            }
        }

        return !hasPositive || positive;
    }

    private bool MatchesStringEntry(RecordInfo npc, SpidStringFilter f)
    {
        var text = f.Text;

        // Check NPC EditorId
        if (!string.IsNullOrEmpty(npc.ResolvedEditorId))
        {
            if (f.Modifier == SpidFilterModifier.Substring)
            {
                if (npc.ResolvedEditorId.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (npc.ResolvedEditorId.Equals(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check NPC display name
        if (!string.IsNullOrEmpty(npc.ResolvedName))
        {
            if (f.Modifier == SpidFilterModifier.Substring)
            {
                if (npc.ResolvedName.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (npc.ResolvedName.Equals(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check if text is a keyword EditorId that this NPC has
        if (npc.Attributes != null)
        {
            foreach (var (kwPlugin, kwLocalId) in npc.Attributes.Keywords)
            {
                var kwEid = _library.ResolveEditorId(kwPlugin, kwLocalId.ToString("X"));
                if (!string.IsNullOrEmpty(kwEid))
                {
                    if (f.Modifier == SpidFilterModifier.Substring)
                    {
                        if (kwEid.Contains(text, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    else if (kwEid.Equals(text, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    // ── FormFilter evaluation ─────────────────────────────────────────────────

    private bool MatchesFormFilters(RecordInfo npc, List<SpidFormFilter> filters)
    {
        bool hasPositive = false;
        bool positive    = false;

        foreach (var f in filters)
        {
            bool matched = MatchesFormEntry(npc, f);

            switch (f.Modifier)
            {
                case SpidFilterModifier.Not:
                    if (matched) return false;
                    break;
                case SpidFilterModifier.All:
                    hasPositive = true;
                    if (!matched) return false;
                    positive = true;
                    break;
                default:
                    hasPositive = true;
                    if (matched) positive = true;
                    break;
            }
        }

        return !hasPositive || positive;
    }

    private bool MatchesFormEntry(RecordInfo npc, SpidFormFilter f)
    {
        if (npc.Attributes == null) return false;

        // Resolve the filter to a (plugin, localId) pair when possible
        (string plugin, uint localId)? filterKey = null;

        if (f.Plugin != null && f.FormId != null &&
            uint.TryParse(f.FormId, NumberStyles.HexNumber, null, out var parsedId))
        {
            filterKey = (f.Plugin.ToLowerInvariant(), parsedId & 0x00FFFFFF);
        }

        if (filterKey.HasValue)
        {
            var (fp, fl) = filterKey.Value;

            // Check factions
            foreach (var faction in npc.Attributes.Factions)
                if (faction.plugin.Equals(fp, StringComparison.OrdinalIgnoreCase) && faction.localId == fl)
                    return true;

            // Check race
            if (npc.Attributes.Race.HasValue)
            {
                var r = npc.Attributes.Race.Value;
                if (r.plugin.Equals(fp, StringComparison.OrdinalIgnoreCase) && r.localId == fl)
                    return true;
            }

            // Check class
            if (npc.Attributes.Class.HasValue)
            {
                var c = npc.Attributes.Class.Value;
                if (c.plugin.Equals(fp, StringComparison.OrdinalIgnoreCase) && c.localId == fl)
                    return true;
            }

            // Check keywords
            foreach (var kw in npc.Attributes.Keywords)
                if (kw.plugin.Equals(fp, StringComparison.OrdinalIgnoreCase) && kw.localId == fl)
                    return true;
        }
        else if (!string.IsNullOrEmpty(f.EditorId))
        {
            // EditorId-based comparison — resolve attribute EditorIds and compare
            return MatchesAttributeEditorId(npc, f.EditorId);
        }

        return false;
    }

    private bool MatchesAttributeEditorId(RecordInfo npc, string editorId)
    {
        if (npc.Attributes == null) return false;

        if (npc.Attributes.Race.HasValue)
        {
            var eid = _library.ResolveEditorId(
                npc.Attributes.Race.Value.plugin,
                npc.Attributes.Race.Value.localId.ToString("X"));
            if (editorId.Equals(eid, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (npc.Attributes.Class.HasValue)
        {
            var eid = _library.ResolveEditorId(
                npc.Attributes.Class.Value.plugin,
                npc.Attributes.Class.Value.localId.ToString("X"));
            if (editorId.Equals(eid, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var faction in npc.Attributes.Factions)
        {
            var eid = _library.ResolveEditorId(faction.plugin, faction.localId.ToString("X"));
            if (editorId.Equals(eid, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var kw in npc.Attributes.Keywords)
        {
            var eid = _library.ResolveEditorId(kw.plugin, kw.localId.ToString("X"));
            if (editorId.Equals(eid, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ── Trait filter evaluation ───────────────────────────────────────────────

    private static bool MatchesTrait(RecordInfo npc, SpidTraitFilter trait)
    {
        if (trait.Male.HasValue && npc.Attributes?.IsMale.HasValue == true)
        {
            if (npc.Attributes.IsMale.Value != trait.Male.Value) return false;
        }
        // Unique, Child, Leveled, Summonable, Teammate, Dead are not yet stored in attributes.
        // They are skipped here; if a trait requires them, the NPC is a candidate by default.
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns true if the list contains at least one entry that is not a direct FormId ref.
    private static bool HasFilterEntries(List<SpidStringFilter> filters)
    {
        foreach (var f in filters)
            if (f.Plugin == null) return true;
        return false;
    }
}
