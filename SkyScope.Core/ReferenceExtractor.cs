using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

// Walks all parsed config rules and registers every record reference in the library.
// No file I/O — operates purely on already-parsed in-memory data.
public class ReferenceExtractor
{
    public void Extract(
        ModReferenceLibrary    library,
        List<ModConfiguration> skyPatcherConfigs,
        List<DistributionRule>   spidRules,
        List<BosSwapRule>      bosRules)
    {
        // SkyPatcher NPC refs (all rule types)
        foreach (var config in skyPatcherConfigs)
            foreach (var rule in config.Rules)
            {
                foreach (var npcRef in rule.TargetNpcs)
                    RegisterNpcRef(library, npcRef);

                RegisterRuleValue(library, rule);
            }

        // SPID NPC refs (all rule types)
        foreach (var rule in spidRules)
        {
            foreach (var npcRef in rule.TargetNpcs)
                RegisterNpcRef(library, npcRef);

            // Register FormFilter Plugin+FormId refs so attribute display names can be resolved
            foreach (var ff in rule.SpidFormFilters)
                if (ff.Plugin != null && ff.FormId != null)
                    library.RegisterFormIdRef(ff.Plugin, ff.FormId, KnownRecordType.Unknown);

            RegisterRuleValue(library, rule);
        }

        // BOS object refs
        foreach (var rule in bosRules)
            foreach (var obj in rule.OriginalObjects)
                RegisterBosRef(library, obj);
    }

    private static void RegisterNpcRef(ModReferenceLibrary library, NpcReference npcRef)
    {
        switch (npcRef.RefType)
        {
            case NpcRefType.RecordId:
                library.RegisterFormIdRef(npcRef.Plugin, npcRef.FormId, KnownRecordType.Npc);
                break;
            case NpcRefType.EditorId:
                library.RegisterEditorIdRef(npcRef.Identifier, KnownRecordType.Npc);
                break;
            case NpcRefType.Name:
                library.RegisterNameRef(npcRef.Identifier);
                break;
            // LocalFormId: no registration — resolved post-enrichment via bare-hex uniqueness index
        }
    }

    // Registers the rule's value (the distributed record) so its display name can be resolved:
    // spell/perk records for Spell/Perk rules, outfit (OTFT) records for OutfitDefault rules.
    private static void RegisterRuleValue(ModReferenceLibrary library, DistributionRule rule)
    {
        var type = rule.RuleType switch
        {
            RuleType.Spell         => KnownRecordType.Spell,
            RuleType.Perk          => KnownRecordType.Perk,
            RuleType.OutfitDefault => KnownRecordType.Outfit,
            _                      => (KnownRecordType?)null
        };
        if (type is null) return;

        RegisterFormValue(library, rule.RuleValue, type.Value);
    }

    // Registers each Plugin|FormId / 0x~Plugin / plain-EditorId token in a rule value.
    private static void RegisterFormValue(ModReferenceLibrary library, string ruleValue, KnownRecordType type)
    {
        if (string.IsNullOrEmpty(ruleValue)) return;

        foreach (var part in ruleValue.Split(','))
        {
            var token = part.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            // SPID format: 0xABCD~Plugin.esp
            var tildeIdx = token.IndexOf('~');
            if (tildeIdx > 0)
            {
                library.RegisterFormIdRef(
                    token[(tildeIdx + 1)..].Trim(),
                    token[..tildeIdx].Trim(),
                    type);
                continue;
            }

            // SkyPatcher format: Plugin.esp|0xABCD
            var pipeIdx = token.IndexOf('|');
            if (pipeIdx > 0)
            {
                library.RegisterFormIdRef(
                    token[..pipeIdx].Trim(),
                    token[(pipeIdx + 1)..].Trim(),
                    type);
                continue;
            }

            // Plain EditorId (not 0x or decimal)
            if (!token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !uint.TryParse(token, out _))
                library.RegisterEditorIdRef(token, type);
        }
    }

    private static void RegisterBosRef(ModReferenceLibrary library, BosObjectRef objRef)
    {
        switch (objRef.RefType)
        {
            case BosRefType.FormIdWithPlugin:
                library.RegisterFormIdRef(objRef.Plugin, objRef.FormId, KnownRecordType.Unknown);
                break;
            case BosRefType.EditorId:
                library.RegisterEditorIdRef(objRef.Identifier, KnownRecordType.Unknown);
                break;
            // BareHex: no targeted enrichment possible without plugin context
        }
    }
}
