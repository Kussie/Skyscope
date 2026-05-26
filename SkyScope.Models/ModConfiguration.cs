using System.Collections.Generic;
using System.Globalization;

namespace SkyScope.Models;

public enum NpcRefType
{
    RecordId,
    EditorId,
    Name
}

public enum RuleType
{
    Appearance,   // copyVisualStyle
    Skin,         // skin
    OutfitDefault // outfitDefault
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
        NpcRefType.RecordId => $"RID:{Plugin.ToLowerInvariant()}|{NormalizeFormId(FormId)}",
        NpcRefType.EditorId => $"EID:{Identifier.ToLowerInvariant()}",
        NpcRefType.Name     => $"NAME:{Identifier.ToLowerInvariant()}",
        _                   => string.Empty
    };

    private static string NormalizeFormId(string formId)
    {
        if (uint.TryParse(formId, NumberStyles.HexNumber, null, out var val))
            return val.ToString("X");
        return formId.ToUpperInvariant();
    }
}

public class SkyPatcherRule
{
    public List<NpcReference> TargetNpcs { get; set; } = new();
    public RuleType RuleType { get; set; }
    public string  RuleValue     { get; set; } = string.Empty;
    public string  SourceFile    { get; set; } = string.Empty;
    public int     LineNumber    { get; set; }
    public string? PrecedingLine { get; set; }  // null = first line of file
    public string  LineText      { get; set; } = string.Empty;
    public string? FollowingLine { get; set; }  // null = last line of file
}

public class ConflictSource
{
    public string  FilePath      { get; set; } = string.Empty;
    public int     LineNumber    { get; set; }
    public string? PrecedingLine { get; set; }
    public string  ConflictLine  { get; set; } = string.Empty;
    public string? FollowingLine { get; set; }
    public string  RuleValue     { get; set; } = string.Empty;
}

public class ModConfiguration
{
    public string ModName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<SkyPatcherRule> Rules { get; set; } = new();
}

public class ConflictEntry
{
    public NpcReference NpcRef { get; set; } = new();
    public List<ConflictSource> Sources { get; set; } = new();

    public string? ResolvedName     { get; set; }
    public string? ResolvedEditorId { get; set; }

    // Prefers EditorId; falls back to Plugin|FormId or raw Identifier
    public string DisplayName => ResolvedEditorId ?? NpcRef.DisplayText;
}

public class ConflictSummary
{
    public List<ConflictEntry> AppearanceConflicts { get; set; } = new();
    public List<ConflictEntry> SkinConflicts { get; set; } = new();
    public List<ConflictEntry> OutfitDefaultConflicts { get; set; } = new();

    public int TotalConflicts =>
        AppearanceConflicts.Count + SkinConflicts.Count + OutfitDefaultConflicts.Count;

    public int TotalFilesScanned { get; set; }
}
