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
        List<SkyPatcherRule>   spidRules,
        List<BosSwapRule>      bosRules)
    {
        // SkyPatcher NPC refs (all rule types)
        foreach (var config in skyPatcherConfigs)
            foreach (var rule in config.Rules)
            {
                foreach (var npcRef in rule.TargetNpcs)
                    RegisterNpcRef(library, npcRef);

                if (rule.RuleType is RuleType.Spell or RuleType.Perk)
                    RegisterSpellPerkValue(library, rule.RuleValue);
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

            if (rule.RuleType is RuleType.Spell or RuleType.Perk)
                RegisterSpellPerkValue(library, rule.RuleValue);
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

    private static void RegisterSpellPerkValue(ModReferenceLibrary library, string ruleValue)
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
                    KnownRecordType.Spell);
                continue;
            }

            // SkyPatcher format: Plugin.esp|0xABCD
            var pipeIdx = token.IndexOf('|');
            if (pipeIdx > 0)
            {
                library.RegisterFormIdRef(
                    token[..pipeIdx].Trim(),
                    token[(pipeIdx + 1)..].Trim(),
                    KnownRecordType.Spell);
                continue;
            }

            // Plain EditorId (not 0x or decimal)
            if (!token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !uint.TryParse(token, out _))
                library.RegisterEditorIdRef(token, KnownRecordType.Spell);
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
