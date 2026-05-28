using System;
using System.Collections.Generic;

namespace SkyScope.Models;

public enum NpcRefType
{
    RecordId,
    EditorId,
    Name,
    LocalFormId  // bare 0x... hex with no plugin context — resolved across all loaded plugins
}

public enum RuleType
{
    Appearance,   // copyVisualStyle
    Skin,         // skin
    OutfitDefault, // outfitDefault
    Spell,        // spellsToAdd (SkyPatcher) / Spell= (SPID)
    Perk          // perksToAdd  (SkyPatcher) / Perk=  (SPID)
}

public class NpcReference
{
    public NpcRefType RefType { get; set; }

    // RecordId fields
    public string Plugin { get; set; } = string.Empty;
    public string FormId { get; set; } = string.Empty;

    // EditorId or Name field
    public string Identifier { get; set; } = string.Empty;

    public string DisplayText => RefType == NpcRefType.RecordId
        ? $"{Plugin}|{FormId}"
        : Identifier;

    public string NormalizedKey => RefType switch
    {
        NpcRefType.RecordId    => $"RID:{Plugin.ToLowerInvariant()}|{FormIdUtils.NormalizeFormId(FormId)}",
        NpcRefType.EditorId    => $"EID:{Identifier.ToLowerInvariant()}",
        NpcRefType.Name        => $"NAME:{Identifier.ToLowerInvariant()}",
        NpcRefType.LocalFormId => $"LFI:{Identifier.ToLowerInvariant()}",
        _                      => string.Empty
    };
}

public class SkyPatcherRule
{
    public List<NpcReference> TargetNpcs { get; set; } = [];
    public RuleType RuleType { get; set; }
    public string  RuleValue     { get; set; } = string.Empty;
    public string  SourceFile    { get; set; } = string.Empty;
    public int     LineNumber    { get; set; }
    public string? PrecedingLine { get; set; }  // null = first line of file
    public string  LineText      { get; set; } = string.Empty;
    public string? FollowingLine { get; set; }  // null = last line of file
    public string  SourceTool    { get; set; } = "SkyPatcher";
    public int?    SpidChance    { get; set; }  // null = not a SPID rule; 0-100 when set
}

public class ConflictSource
{
    public string  FilePath      { get; set; } = string.Empty;
    public int     LineNumber    { get; set; }
    public string? PrecedingLine { get; set; }
    public string  ConflictLine  { get; set; } = string.Empty;
    public string? FollowingLine { get; set; }
    public string  RuleValue     { get; set; } = string.Empty;
    public string  SourceTool         { get; set; } = "SkyPatcher";
    public int?    SpidChance         { get; set; }
    public string? SpidNpcIdentifier  { get; set; }  // exact text from SPID file that matched this conflict
}

public class ModConfiguration
{
    public string ModName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<SkyPatcherRule> Rules { get; set; } = [];
}

public class ConflictEntry
{
    public NpcReference NpcRef { get; set; } = new();
    public List<ConflictSource> Sources { get; set; } = [];

    public string? ResolvedName     { get; set; }
    public string? ResolvedEditorId { get; set; }

    public string DisplayName
    {
        get
        {
            var primary = ResolvedEditorId ?? NpcRef.DisplayText;
            // Only append the full name when it differs from the EditorId — for localised plugins
            // (e.g. Skyrim.esm) ResolveName falls back to the EditorId, so they'd be identical.
            if (!string.IsNullOrEmpty(ResolvedName) &&
                !string.Equals(ResolvedName, primary, StringComparison.OrdinalIgnoreCase))
                return $"{primary} ({ResolvedName})";
            return primary;
        }
    }
}

public class ConflictSummary
{
    public List<ConflictEntry> AppearanceConflicts    { get; set; } = [];
    public List<ConflictEntry> SkinConflicts          { get; set; } = [];
    public List<ConflictEntry> OutfitDefaultConflicts { get; set; } = [];
    public List<ConflictEntry> SpellConflicts         { get; set; } = [];
    public List<ConflictEntry> PerkConflicts          { get; set; } = [];

    public int TotalConflicts =>
        AppearanceConflicts.Count + SkinConflicts.Count + OutfitDefaultConflicts.Count +
        SpellConflicts.Count + PerkConflicts.Count;

    public int TotalFilesScanned { get; set; }
}
