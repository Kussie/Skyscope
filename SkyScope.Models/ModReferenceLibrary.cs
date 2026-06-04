using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkyScope.Models;

public enum KnownRecordType { Unknown, Npc, Spell, Perk, Outfit, Static, Furniture, Door, Activator, Container, Other }

// NPC attributes extracted from the plugin binary (race, class, factions, keywords, gender).
// Populated by PluginEnricher via EsmNpcParser; used by SpidFilterEvaluator.
public class NpcAttributeSet
{
    public (string plugin, uint localId)? Race    { get; set; }
    public (string plugin, uint localId)? Class   { get; set; }
    public List<(string plugin, uint localId)> Keywords { get; } = [];
    public List<(string plugin, uint localId)> Factions { get; } = [];
    public bool? IsMale { get; set; }   // null = unknown; true = male; false = female
}

public class RecordInfo
{
    public string?         Plugin       { get; set; }
    public string?         FormId       { get; set; }
    public string?         EditorId     { get; set; }
    public KnownRecordType ExpectedType { get; set; } = KnownRecordType.Unknown;

    public string?         ResolvedEditorId { get; set; }
    public string?         ResolvedName     { get; set; }
    public KnownRecordType ActualType       { get; set; } = KnownRecordType.Unknown;
    public bool            IsEnriched       { get; set; }
    public NpcAttributeSet? Attributes      { get; set; }

    public string DisplayText =>
        ResolvedName ?? ResolvedEditorId ?? EditorId
        ?? (Plugin != null && FormId != null ? $"{Plugin}|0x{FormId}" : FormId ?? "?");
}

public class ModReferenceLibrary
{
    // Primary lookup: (plugin.lower, localFormId) → RecordInfo
    private readonly Dictionary<(string, uint), RecordInfo> _byFormId = new();

    // EditorId (case-insensitive) → RecordInfo  (NPC + Spell/Perk)
    private readonly Dictionary<string, RecordInfo> _byEditorId = new(StringComparer.OrdinalIgnoreCase);

    // NPC display name (case-insensitive) → RecordInfo  (SPID StringFilter targets)
    private readonly Dictionary<string, RecordInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    // localFormId → RecordInfo?  — null when same local ID exists in multiple plugins (ambiguous)
    private readonly Dictionary<uint, RecordInfo?> _byBareHex = new();

    // Validated NPC editorIds — used to reject SPID values that are keywords/factions/etc.
    private readonly HashSet<string> _npcEditorIds = new(StringComparer.OrdinalIgnoreCase);

    // All plugin filenames present in the load order
    private readonly HashSet<string> _loadedPlugins = new(StringComparer.OrdinalIgnoreCase);

    // (plugin.lower, localFormId) pairs registered as BOS refs — drives targeted object scan
    private readonly HashSet<(string, uint)> _bosRefs = new();

    // Plugins that have at least one registered BOS FormId ref
    private readonly HashSet<string> _bosRefPlugins = new(StringComparer.OrdinalIgnoreCase);

    // Plugins that have at least one SPEL/PERK ref (for targeted spell/perk enrichment)
    private readonly HashSet<string> _spelPerkRefPlugins = new(StringComparer.OrdinalIgnoreCase);

    // Plugins that have at least one OTFT ref (for targeted outfit enrichment)
    private readonly HashSet<string> _outfitRefPlugins = new(StringComparer.OrdinalIgnoreCase);

    // (originPlugin.lower, localFormId) → ordered list of plugins that override this NPC's
    // appearance, in load order. Populated by PluginEnricher; read by ConflictDetector to add
    // plugin appearance overhauls as conflict sources.
    private readonly Dictionary<(string, uint), List<(string OverridePlugin, int LoadOrderIndex)>>
        _appearanceOverrides = new();

    private int _npcRecordCount;

    public int NpcRecordCount => _npcRecordCount;

    // ── Initialisation ────────────────────────────────────────────────────────

    public void SetLoadedPlugins(IEnumerable<string> pluginNames)
    {
        foreach (var name in pluginNames)
            _loadedPlugins.Add(name);
    }

    // ── Registration (called by ReferenceExtractor) ───────────────────────────

    public void RegisterFormIdRef(string plugin, string formIdHex, KnownRecordType type)
    {
        if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(formIdHex)) return;

        var hex = formIdHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? formIdHex[2..] : formIdHex;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, null, out var raw)) return;
        uint localFormId = raw & 0x00FFFFFF;
        var  key         = (plugin.ToLowerInvariant(), localFormId);

        if (!_byFormId.ContainsKey(key))
            _byFormId[key] = new RecordInfo
            {
                Plugin       = plugin,
                FormId       = localFormId.ToString("X"),
                ExpectedType = type
            };

        switch (type)
        {
            case KnownRecordType.Npc:
                break; // NPCs are covered by the full NPC_ scan
            case KnownRecordType.Spell:
            case KnownRecordType.Perk:
                _spelPerkRefPlugins.Add(plugin);
                break;
            case KnownRecordType.Outfit:
                _outfitRefPlugins.Add(plugin);
                break;
            default: // Unknown + base-object types → targeted BOS object scan
                _bosRefs.Add(key);
                _bosRefPlugins.Add(plugin);
                break;
        }
    }

    public void RegisterEditorIdRef(string editorId, KnownRecordType type)
    {
        if (string.IsNullOrEmpty(editorId) || _byEditorId.ContainsKey(editorId)) return;
        _byEditorId[editorId] = new RecordInfo { EditorId = editorId, ExpectedType = type };
    }

    public void RegisterNameRef(string name)
    {
        if (string.IsNullOrEmpty(name) || _byName.ContainsKey(name)) return;
        _byName[name] = new RecordInfo { ExpectedType = KnownRecordType.Npc };
    }

    // ── NPC Enrichment (called by PluginEnricher for every NPC_ record) ───────

    public void EnrichNpc(string originalPlugin, uint localFormId, string? editorId, string? name,
                          NpcAttributeSet? attributes = null)
    {
        var key = (originalPlugin.ToLowerInvariant(), localFormId);

        if (!_byFormId.TryGetValue(key, out var ri))
        {
            ri = new RecordInfo
            {
                Plugin       = originalPlugin,
                FormId       = localFormId.ToString("X"),
                ExpectedType = KnownRecordType.Npc,
                ActualType   = KnownRecordType.Npc
            };
            _byFormId[key] = ri;
        }

        if (!string.IsNullOrEmpty(editorId)) ri.ResolvedEditorId = editorId;
        if (!string.IsNullOrEmpty(name))     ri.ResolvedName     = name;
        if (attributes != null)              ri.Attributes       = attributes;
        ri.ActualType = KnownRecordType.Npc;

        if (!ri.IsEnriched)
        {
            ri.IsEnriched = true;
            _npcRecordCount++;
        }

        if (!string.IsNullOrEmpty(editorId))
        {
            _npcEditorIds.Add(editorId);
            // Distinct records (vanilla + replacer/converter proxies) can share an EditorId.
            // Keep the one with the best name: a real display name (differs from the EditorId)
            // beats a name that merely echoes the EditorId, which beats no name at all. This
            // stops a proxy whose FULL just copies the EditorId from hiding the real vanilla name.
            if (!_byEditorId.TryGetValue(editorId, out var prior)
                || NameQuality(ri, editorId) >= NameQuality(prior, editorId))
                _byEditorId[editorId] = ri;
        }

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(editorId))
            _byName[name] = ri;
    }

    // 2 = real display name (differs from EditorId), 1 = name echoes EditorId, 0 = no name.
    private static int NameQuality(RecordInfo ri, string editorId)
    {
        if (string.IsNullOrEmpty(ri.ResolvedName)) return 0;
        return string.Equals(ri.ResolvedName, editorId, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    // ── Appearance overrides (plugin overhauls) ──────────────────────────────

    // Records that plugin `overridePlugin` overrides NPC (originPlugin, localFormId) with its
    // own appearance data. Appended in load order; duplicate plugins for the same record are
    // ignored. Read back via AppearanceOverrides during conflict detection.
    public void AddAppearanceOverride(string originPlugin, uint localFormId, string overridePlugin, int loadOrderIndex)
    {
        if (string.IsNullOrEmpty(originPlugin) || string.IsNullOrEmpty(overridePlugin)) return;

        var key = (originPlugin.ToLowerInvariant(), localFormId);
        if (!_appearanceOverrides.TryGetValue(key, out var list))
        {
            list = new List<(string, int)>();
            _appearanceOverrides[key] = list;
        }

        if (!list.Any(e => string.Equals(e.OverridePlugin, overridePlugin, StringComparison.OrdinalIgnoreCase)))
            list.Add((overridePlugin, loadOrderIndex));
    }

    // (originPlugin.lower, localFormId) → ordered overriding plugins. Consumed by ConflictDetector.
    public IReadOnlyDictionary<(string OriginPlugin, uint LocalFormId), List<(string OverridePlugin, int LoadOrderIndex)>>
        AppearanceOverrides => _appearanceOverrides;

    // Returns all enriched NPC RecordInfos. Used by SpidFilterEvaluator.
    public IEnumerable<RecordInfo> GetAllNpcs() =>
        _byFormId.Values.Where(ri => ri.ActualType == KnownRecordType.Npc && ri.IsEnriched);

    // Build bare-hex uniqueness index; call once after all NPC enrichment is done.
    public void FinalizeNpcBareHexIndex()
    {
        foreach (var ((_, localFormId), ri) in _byFormId)
        {
            if (ri.ActualType != KnownRecordType.Npc) continue;

            if (!_byBareHex.TryGetValue(localFormId, out var existing))
                _byBareHex[localFormId] = ri;
            else if (existing != null)
                _byBareHex[localFormId] = null;  // ambiguous: same local ID in multiple plugins
        }
    }

    // ── Spell/Perk Enrichment (display-name resolution for rule values) ───────

    public void EnrichSpellPerk(string originalPlugin, uint localFormId, string? editorId, string? name)
    {
        var key = (originalPlugin.ToLowerInvariant(), localFormId);

        if (!_byFormId.TryGetValue(key, out var ri))
        {
            // Not registered as a spell/perk ref — ignore (targeted enrichment only)
            return;
        }

        if (!string.IsNullOrEmpty(editorId)) ri.ResolvedEditorId = editorId;
        if (!string.IsNullOrEmpty(name))     ri.ResolvedName     = name;

        if (ri.ActualType == KnownRecordType.Unknown)
            ri.ActualType = ri.ExpectedType;

        if (!string.IsNullOrEmpty(editorId))
            _byEditorId.TryAdd(editorId, ri);
    }

    // ── Base Object Enrichment (targeted scan for BOS refs) ──────────────────

    public void EnrichObject(string originalPlugin, uint localFormId, string? editorId, string? name)
    {
        var key = (originalPlugin.ToLowerInvariant(), localFormId);
        if (!_byFormId.TryGetValue(key, out var ri)) return;

        if (!string.IsNullOrEmpty(editorId)) ri.ResolvedEditorId = editorId;
        if (!string.IsNullOrEmpty(name))     ri.ResolvedName     = name;

        if (!string.IsNullOrEmpty(editorId))
            _byEditorId.TryAdd(editorId, ri);
    }

    // ── BOS targeted-scan helpers ─────────────────────────────────────────────

    public IReadOnlySet<string> BosRefPlugins => _bosRefPlugins;

    public bool HasBosRef(string originalPlugin, uint localFormId) =>
        _bosRefs.Contains((originalPlugin.ToLowerInvariant(), localFormId));

    public IReadOnlySet<string> SpelPerkRefPlugins => _spelPerkRefPlugins;

    public IReadOnlySet<string> OutfitRefPlugins => _outfitRefPlugins;

    // ── Queries: NPC conflict detection (replaces NpcNameDatabase) ────────────

    public string GetCanonicalKey(NpcReference npcRef)
    {
        switch (npcRef.RefType)
        {
            case NpcRefType.RecordId:
            {
                if (!uint.TryParse(npcRef.FormId, NumberStyles.HexNumber, null, out var raw))
                    return string.Empty;
                uint localFormId = raw & 0x00FFFFFF;
                var  key         = (npcRef.Plugin.ToLowerInvariant(), localFormId);
                if (_byFormId.TryGetValue(key, out var ri) && !string.IsNullOrEmpty(ri.ResolvedEditorId))
                    return $"EID:{ri.ResolvedEditorId.ToLowerInvariant()}";
                return string.Empty;
            }
            case NpcRefType.Name:
            {
                if (_byName.TryGetValue(npcRef.Identifier, out var ri) && !string.IsNullOrEmpty(ri.ResolvedEditorId))
                    return $"EID:{ri.ResolvedEditorId.ToLowerInvariant()}";
                // Value might itself be an NPC EditorId
                if (_npcEditorIds.Contains(npcRef.Identifier))
                    return $"EID:{npcRef.Identifier.ToLowerInvariant()}";
                return string.Empty;
            }
            case NpcRefType.EditorId:
                return _npcEditorIds.Contains(npcRef.Identifier)
                    ? $"EID:{npcRef.Identifier.ToLowerInvariant()}"
                    : string.Empty;
            case NpcRefType.LocalFormId:
            {
                var hexStr = npcRef.Identifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                             ? npcRef.Identifier[2..] : npcRef.Identifier;
                if (!uint.TryParse(hexStr, NumberStyles.HexNumber, null, out var raw))
                    return string.Empty;
                uint localFormId = raw & 0x00FFFFFF;
                if (_byBareHex.TryGetValue(localFormId, out var ri) && ri != null
                    && !string.IsNullOrEmpty(ri.ResolvedEditorId))
                    return $"EID:{ri.ResolvedEditorId.ToLowerInvariant()}";
                return string.Empty;
            }
            default:
                return npcRef.NormalizedKey;
        }
    }

    public string? ResolveName(string plugin, string formIdHex)
    {
        var (name, eid) = LookupNpc(plugin, formIdHex);
        return name ?? eid;
    }

    public string? ResolveEditorId(string plugin, string formIdHex) =>
        LookupNpc(plugin, formIdHex).editorId;

    public (string? editorId, string? name) ResolveByLocalFormId(string hexFormId)
    {
        var hex = hexFormId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hexFormId[2..] : hexFormId;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, null, out var raw)) return (null, null);
        uint localFormId = raw & 0x00FFFFFF;
        if (!_byBareHex.TryGetValue(localFormId, out var ri) || ri == null) return (null, null);
        return (ri.ResolvedEditorId, ri.ResolvedName);
    }

    public string? FindEditorIdByName(string name) =>
        _byName.TryGetValue(name, out var ri) ? ri.ResolvedEditorId : null;

    // Resolves an NPC's full display name from its EditorId (used for EditorId-keyed conflicts).
    public string? ResolveNameByEditorId(string editorId) =>
        _byEditorId.TryGetValue(editorId, out var ri) ? ri.ResolvedName : null;

    // Resolves the owning plugin of a record from its EditorId — for rule references that name
    // a record by EditorId only (no inline Plugin|FormId). Returns the origin of whichever record
    // won the EditorId slot; null when the EditorId wasn't enriched (e.g. plugin not loaded).
    public string? ResolvePluginByEditorId(string editorId) =>
        _byEditorId.TryGetValue(editorId, out var ri) ? ri.Plugin : null;

    // Resolves the local FormId (hex) of a record from its EditorId; null when not enriched.
    public string? ResolveFormIdByEditorId(string editorId) =>
        _byEditorId.TryGetValue(editorId, out var ri) ? ri.FormId : null;

    public bool IsNpcEditorId(string editorId) => _npcEditorIds.Contains(editorId);

    // ── Display name for BOS conflict entries ─────────────────────────────────

    public string? GetDisplayName(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return null;

        if (normalizedKey.StartsWith("RID:", StringComparison.OrdinalIgnoreCase))
        {
            var rest     = normalizedKey[4..];
            var pipeIdx  = rest.IndexOf('|');
            if (pipeIdx <= 0) return null;
            var plugin   = rest[..pipeIdx];
            var formIdStr = rest[(pipeIdx + 1)..];
            if (!uint.TryParse(formIdStr, NumberStyles.HexNumber, null, out var raw)) return null;
            uint localFormId = raw & 0x00FFFFFF;
            return _byFormId.TryGetValue((plugin, localFormId), out var ri) ? ri.DisplayText : null;
        }

        if (normalizedKey.StartsWith("EID:", StringComparison.OrdinalIgnoreCase))
        {
            var eid = normalizedKey[4..];
            return _byEditorId.TryGetValue(eid, out var ri) ? ri.DisplayText : null;
        }

        return null;
    }

    // ── Load-order filter (replaces FormNameDatabase.HasAnyLoadedPlugin) ──────

    public bool HasAnyLoadedPlugin(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) return false;

        foreach (var part in ruleValue.Split(','))
        {
            var token = part.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            var tildeIdx = token.IndexOf('~');
            if (tildeIdx > 0)
            {
                if (_loadedPlugins.Contains(token[(tildeIdx + 1)..].Trim())) return true;
                continue;
            }

            var pipeIdx = token.IndexOf('|');
            if (pipeIdx > 0)
            {
                if (_loadedPlugins.Contains(token[..pipeIdx].Trim())) return true;
                continue;
            }

            // EditorId or unrecognised format — assume potentially valid
            return true;
        }
        return false;
    }

    // ── Spell/Perk display enrichment (replaces FormNameDatabase.ResolveRuleValue) ─

    public string ResolveRuleValue(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) return ruleValue;

        var parts    = ruleValue.Split(',');
        var resolved = parts.Select(p => ResolveRuleToken(p.Trim()) ?? p.Trim());
        return string.Join(", ", resolved);
    }

    private string? ResolveRuleToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var tildeIdx = token.IndexOf('~');
        if (tildeIdx > 0)
            return ResolveFormIdDisplay(token[(tildeIdx + 1)..].Trim(), token[..tildeIdx].Trim());

        var pipeIdx = token.IndexOf('|');
        if (pipeIdx > 0)
            return ResolveFormIdDisplay(token[..pipeIdx].Trim(), token[(pipeIdx + 1)..].Trim());

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        if (uint.TryParse(token, out _)) return null;

        // Plain EditorId
        if (_byEditorId.TryGetValue(token, out var ri))
        {
            var name = ri.ResolvedName;
            var eid  = ri.ResolvedEditorId ?? token;
            return (!string.IsNullOrEmpty(name) && !string.Equals(name, eid, StringComparison.OrdinalIgnoreCase))
                ? $"{name} ({eid})"
                : eid;
        }
        return token;
    }

    private string? ResolveFormIdDisplay(string plugin, string formIdStr)
    {
        if (formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            formIdStr = formIdStr[2..];
        if (!uint.TryParse(formIdStr, NumberStyles.HexNumber, null, out var raw)) return null;
        uint localFormId = raw & 0x00FFFFFF;
        var  key         = (plugin.ToLowerInvariant(), localFormId);
        if (!_byFormId.TryGetValue(key, out var ri)) return null;
        var eid  = ri.ResolvedEditorId;
        var name = ri.ResolvedName;
        if (!string.IsNullOrEmpty(name) && !string.Equals(name, eid, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({eid})";
        return eid ?? name;
    }

    private (string? name, string? editorId) LookupNpc(string plugin, string formIdHex)
    {
        if (!uint.TryParse(formIdHex, NumberStyles.HexNumber, null, out var formId)) return (null, null);
        uint localFormId = formId & 0x00FFFFFF;
        var  key         = (plugin.ToLowerInvariant(), localFormId);
        return _byFormId.TryGetValue(key, out var ri) ? (ri.ResolvedName, ri.ResolvedEditorId) : (null, null);
    }
}
