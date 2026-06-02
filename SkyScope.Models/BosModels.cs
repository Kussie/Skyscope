using System.Collections.Generic;

namespace SkyScope.Models;

public enum BosRefType { FormIdWithPlugin, BareHex, EditorId }

public class BosObjectRef
{
    public BosRefType RefType    { get; set; }
    public string     Plugin     { get; set; } = "";   // FormIdWithPlugin only
    public string     FormId     { get; set; } = "";   // FormIdWithPlugin only
    public string     Identifier { get; set; } = "";   // EditorId or BareHex

    public string DisplayText => RefType == BosRefType.FormIdWithPlugin
        ? $"{Plugin}|{FormId}"
        : Identifier;

    public string NormalizedKey => RefType switch
    {
        BosRefType.FormIdWithPlugin => $"RID:{Plugin.ToLowerInvariant()}|{FormIdUtils.NormalizeFormId(FormId)}",
        BosRefType.EditorId         => $"EID:{Identifier.ToLowerInvariant()}",
        BosRefType.BareHex          => $"HEX:{Identifier.ToLowerInvariant()}",
        _                           => ""
    };
}

public class BosSwapRule
{
    public List<BosObjectRef> OriginalObjects  { get; set; } = [];
    public string  SwapTarget                  { get; set; } = "";
    public string  SourceFile                  { get; set; } = "";
    public int     LineNumber                  { get; set; }
    public string  LineText                    { get; set; } = "";
    public string? PrecedingLine               { get; set; }
    public string? FollowingLine               { get; set; }
    public string? ConditionalSection          { get; set; }   // null = global
    public string  SectionType                 { get; set; } = "Forms";

    // BOS reference filter — the "properties" field of a swap rule (e.g. posA(x,y,z), flags(0x..),
    // bnds(...)). Constrains the swap to specific world references rather than the base form. Two
    // rules with different PropertiesFilter values target disjoint sets of references and don't
    // truly conflict. Empty string = unfiltered (applies to every reference of the base form).
    public string  PropertiesFilter            { get; set; } = "";

    // Optional swap chance. Recognised forms: bare number (e.g. "50" / "7.6"), chanceA(N), or
    // chanceR(N), where N may be fractional. Null when the rule has no chance field.
    public double? Chance                      { get; set; }
}

public class BosConflictSource
{
    public string  FilePath           { get; set; } = "";
    public int     LineNumber         { get; set; }
    public string? PrecedingLine      { get; set; }
    public string  LineText           { get; set; } = "";
    public string? FollowingLine      { get; set; }
    public string  SwapTarget         { get; set; } = "";
    public string? ConditionalSection { get; set; }
    public string  SectionType        { get; set; } = "Forms";
    public double? Chance             { get; set; }
}

public class BosConflictEntry
{
    public BosObjectRef             ObjectRef { get; set; } = new();
    public List<BosConflictSource>  Sources   { get; set; } = [];
    public string? ResolvedName               { get; set; }

    public string DisplayName => ResolvedName ?? ObjectRef.DisplayText;
}

public class BosConflictSummary
{
    public List<BosConflictEntry> SwapConflicts { get; set; } = [];
    public int FilesScanned                     { get; set; }
    public int TotalConflicts                   => SwapConflicts.Count;
}
