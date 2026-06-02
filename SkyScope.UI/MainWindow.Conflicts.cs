using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

// Conflict post-processing: name resolution, SPID merge, name backfill, and dedup.
public partial class MainWindow
{
    private static void ResolveNames(ConflictSummary summary, ModReferenceLibrary library)
    {
        foreach (var entry in AllEntries(summary))
        {
            if (entry.NpcRef.RefType == NpcRefType.RecordId)
            {
                entry.ResolvedName     = library.ResolveName(entry.NpcRef.Plugin, entry.NpcRef.FormId);
                entry.ResolvedEditorId = library.ResolveEditorId(entry.NpcRef.Plugin, entry.NpcRef.FormId);
            }
            else if (entry.NpcRef.RefType == NpcRefType.LocalFormId)
            {
                var (eid, name)        = library.ResolveByLocalFormId(entry.NpcRef.Identifier);
                entry.ResolvedEditorId = eid;
                entry.ResolvedName     = name;
            }
        }
    }

    private static void MergeSpidConflicts(
        ConflictSummary summary,
        List<ModConfiguration> skyPatcherConfigs,
        List<DistributionRule> spidRules,
        ModReferenceLibrary library)
    {
        // Build EditorId index of every SkyPatcher outfit rule (not just conflicting ones).
        var spByEditorId = new Dictionary<string, List<DistributionRule>>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in skyPatcherConfigs)
        {
            foreach (var rule in config.Rules)
            {
                if (rule.RuleType != RuleType.OutfitDefault) continue;

                foreach (var npcRef in rule.TargetNpcs)
                {
                    var eid = npcRef.RefType switch
                    {
                        NpcRefType.RecordId => library.ResolveEditorId(npcRef.Plugin, npcRef.FormId),
                        NpcRefType.EditorId => npcRef.Identifier,
                        NpcRefType.Name     => library.FindEditorIdByName(npcRef.Identifier),
                        _                   => null
                    };

                    if (string.IsNullOrEmpty(eid)) continue;

                    if (!spByEditorId.TryGetValue(eid, out var list))
                        spByEditorId[eid] = list = new();

                    if (!list.Exists(r => string.Equals(r.SourceFile, rule.SourceFile, StringComparison.OrdinalIgnoreCase)))
                        list.Add(rule);
                }
            }
        }

        // Index existing conflict entries by resolved EditorId.
        var conflictByEditorId = new Dictionary<string, ConflictEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in summary.OutfitDefaultConflicts)
        {
            if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
                conflictByEditorId[entry.ResolvedEditorId] = entry;
        }

        // Group SPID rules by resolved EditorId, recording which identifier text matched.
        // StringFilter refs (Name type) go through the name→EditorId reverse lookup first.
        var spidByEditorId = new Dictionary<string, (NpcReference NpcRef, List<(DistributionRule Rule, string Identifier)> Entries)>(StringComparer.OrdinalIgnoreCase);

        void AddSpidEntry(string eid, DistributionRule rule, string identifier)
        {
            if (!spidByEditorId.TryGetValue(eid, out var entry))
            {
                var syntheticRef = new NpcReference { RefType = NpcRefType.EditorId, Identifier = eid };
                spidByEditorId[eid] = (syntheticRef, new());
            }
            var entries = spidByEditorId[eid].Entries;
            if (!entries.Exists(e => string.Equals(e.Rule.SourceFile, rule.SourceFile, StringComparison.OrdinalIgnoreCase)))
                entries.Add((rule, identifier));
        }

        // ── Direct NPC refs (Field 1 FormId refs and legacy Name/EditorId refs) ──
        foreach (var rule in spidRules)
        {
            foreach (var npcRef in rule.TargetNpcs)
            {
                string? eid;
                if (npcRef.RefType == NpcRefType.EditorId)
                {
                    if (!library.IsNpcEditorId(npcRef.Identifier)) continue;
                    eid = npcRef.Identifier;
                }
                else
                {
                    eid = library.FindEditorIdByName(npcRef.Identifier);
                    if (eid == null)
                    {
                        if (library.IsNpcEditorId(npcRef.Identifier))
                            eid = npcRef.Identifier;
                        else
                            continue;
                    }
                }

                AddSpidEntry(eid, rule, npcRef.Identifier);
            }
        }

        // ── Filter-based refs (keyword/faction/race/trait filters via SpidFilterEvaluator) ──
        var evaluator = new SpidFilterEvaluator(library);
        foreach (var rule in spidRules)
        {
            var expandedEids = evaluator.ExpandFilterTargets(rule);
            foreach (var eid in expandedEids)
                AddSpidEntry(eid, rule, eid);
        }

        // Merge.
        foreach (var (eid, (npcRef, spidEntries)) in spidByEditorId)
        {
            spByEditorId.TryGetValue(eid, out var spRules);

            if (conflictByEditorId.TryGetValue(eid, out var existing))
            {
                foreach (var (r, identifier) in spidEntries)
                    existing.Sources.Add(ToConflictSource(r, identifier));
            }
            else
            {
                var totalSources = (spRules?.Count ?? 0) + spidEntries.Count;
                if (totalSources < 2) continue;

                var newEntry = new ConflictEntry
                {
                    NpcRef           = npcRef,
                    ResolvedEditorId = eid,
                };

                if (spRules != null)
                    foreach (var r in spRules)
                        newEntry.Sources.Add(ToConflictSource(r));

                foreach (var (r, identifier) in spidEntries)
                    newEntry.Sources.Add(ToConflictSource(r, identifier));

                summary.OutfitDefaultConflicts.Add(newEntry);
            }
        }
    }

    private static ConflictSource ToConflictSource(DistributionRule rule, string? spidNpcIdentifier = null) => new()
    {
        FilePath           = rule.SourceFile,
        LineNumber         = rule.LineNumber,
        PrecedingLine      = rule.PrecedingLine,
        ConflictLine       = rule.LineText,
        FollowingLine      = rule.FollowingLine,
        RuleValue          = rule.RuleValue,
        SourceTool         = rule.SourceTool,
        SpidChance         = rule.SpidChance,
        SpidNpcIdentifier  = spidNpcIdentifier
    };

    // Fills/upgrades ResolvedName via the EditorId mapping. Needed for EditorId-referenced and
    // SPID-merged conflicts (which carry only an EditorId), and to upgrade FormID-resolved
    // conflicts whose name merely echoes the EditorId — e.g. a replacer proxy referenced by its
    // own FormID whose FULL copies the EditorId, hiding the real vanilla name held under that EditorId.
    private static void FillResolvedNames(ConflictSummary summary, ModReferenceLibrary library)
    {
        foreach (var entry in AllEntries(summary))
        {
            var eid = entry.ResolvedEditorId;
            if (string.IsNullOrEmpty(eid) &&
                entry.NpcRef.RefType is NpcRefType.EditorId or NpcRefType.Name)
                eid = entry.NpcRef.Identifier;
            if (string.IsNullOrEmpty(eid)) continue;

            // Lock in the EditorId reference whenever the identifier is a known NPC EditorId —
            // even if the display name can't be resolved (e.g. localized FULL not extracted from
            // the BSA strings table). This lets downstream FormId lookups succeed for NPCs like
            // Delphine/Lydia whose rule identifier equals their EditorId.
            if (string.IsNullOrEmpty(entry.ResolvedEditorId) && library.IsNpcEditorId(eid))
                entry.ResolvedEditorId = eid;

            // Only fill an empty name or upgrade one that just echoes the EditorId.
            bool weak = string.IsNullOrEmpty(entry.ResolvedName)
                        || string.Equals(entry.ResolvedName, eid, StringComparison.OrdinalIgnoreCase);
            if (!weak) continue;

            var name = library.ResolveNameByEditorId(eid);
            if (!string.IsNullOrEmpty(name))
            {
                entry.ResolvedName = name;
                if (string.IsNullOrEmpty(entry.ResolvedEditorId))
                    entry.ResolvedEditorId = eid;
            }
        }
    }

    // A name-based SPID filter (e.g. "Lisette") matches every NPC record sharing that name —
    // vanilla plus replacer variants with distinct EditorIds. That produces one near-identical
    // conflict entry per record. Collapse entries that share a display name AND an identical set
    // of source rules into a single entry, preferring the canonical record (EditorId == name).
    private static void DeduplicateConflicts(ConflictSummary summary)
    {
        DedupeList(summary.AppearanceConflicts);
        DedupeList(summary.SkinConflicts);
        DedupeList(summary.OutfitDefaultConflicts);
        DedupeList(summary.SpellConflicts);
        DedupeList(summary.PerkConflicts);
    }

    private static void DedupeList(List<ConflictEntry> entries)
    {
        var byKey  = new Dictionary<string, ConflictEntry>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ConflictEntry>();

        foreach (var entry in entries)
        {
            var name = entry.ResolvedName ?? entry.ResolvedEditorId ?? entry.NpcRef.DisplayText;
            var sig  = string.Join(";", entry.Sources
                .Select(s => $"{s.FilePath}|{s.LineNumber}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var key  = $"{name}##{sig}";

            if (byKey.TryGetValue(key, out var existing))
            {
                // Same logical conflict — keep the cleaner representative.
                if (IsCanonical(entry) && !IsCanonical(existing))
                {
                    result[result.IndexOf(existing)] = entry;
                    byKey[key] = entry;
                }
                continue;
            }

            byKey[key] = entry;
            result.Add(entry);
        }

        if (result.Count != entries.Count)
        {
            entries.Clear();
            entries.AddRange(result);
        }
    }

    private static bool IsCanonical(ConflictEntry e) =>
        !string.IsNullOrEmpty(e.ResolvedEditorId) &&
        string.Equals(e.ResolvedEditorId, e.ResolvedName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ConflictEntry> AllEntries(ConflictSummary s) =>
        s.AppearanceConflicts.Concat(s.SkinConflicts).Concat(s.OutfitDefaultConflicts)
         .Concat(s.SpellConflicts).Concat(s.PerkConflicts);
}
