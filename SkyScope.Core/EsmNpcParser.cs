using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkyScope.Models;

namespace SkyScope.Core;

// Extracts NPC data (name, EditorId, race, class, factions, keywords, gender) from a plugin file.
// Only reads the NPC_ top-level group; everything else is skipped for speed.
internal class EsmNpcParser
{
    private static readonly HashSet<string> NpcGroup = new() { "NPC_" };

    // NPC_ subrecords that only appear when the record carries its own face/appearance data
    // (i.e. it isn't inheriting traits from a template). Their mere presence marks the record
    // as an appearance edit — head parts, hair colour, skin tone, face morph/parts, head
    // texture set, and tint layers. Used to tell an appearance overhaul override apart from an
    // override that only touches factions / AI / stats.
    private static readonly HashSet<string> AppearanceTags =
        new() { "PNAM", "HCLF", "QNAM", "NAM9", "NAMA", "FTST", "TINI" };

    internal class NpcEntry
    {
        public string  OriginalPlugin { get; set; } = string.Empty;  // origin master of the form
        public string  ScanningPlugin { get; set; } = string.Empty;  // file this record was read from
        public uint    LocalFormId    { get; set; }
        public string? EditorId       { get; set; }
        public string? FullName       { get; set; }

        // True when this record was read from a plugin other than the form's origin master —
        // i.e. it is an override of an existing NPC rather than a newly-defined one.
        public bool IsOverride =>
            !string.Equals(ScanningPlugin, OriginalPlugin, StringComparison.OrdinalIgnoreCase);

        // True when the record carries appearance subrecords (see AppearanceTags).
        public bool HasAppearanceData { get; set; }

        // Attributes from binary subrecords — resolved to (originalPlugin, localFormId) tuples
        public (string plugin, uint localId)? Race    { get; set; }
        public (string plugin, uint localId)? Class   { get; set; }
        public List<(string plugin, uint localId)> Keywords { get; set; } = [];
        public List<(string plugin, uint localId)> Factions { get; set; } = [];
        public bool? IsMale { get; set; }
    }

    internal class ParseResult
    {
        public List<string>   Masters            { get; } = [];
        public bool           IsLocalised        { get; set; }
        public List<NpcEntry> Npcs               { get; } = [];
        public int            StringTableEntries { get; set; }  // 0 when not localised or file not found
    }

    public ParseResult Parse(
        string filePath,
        Dictionary<string, Dictionary<uint, string>>? bsaStringCache = null)
    {
        var result      = new ParseResult();
        var pluginName  = Path.GetFileName(filePath);

        // The .STRINGS table is needed only for localised plugins and only once we hit an NPC
        // record; load it lazily on first record so non-localised / NPC-free plugins skip the work.
        Dictionary<uint, string>? stringTable = null;
        bool stringTableLoaded = false;

        var (_, masters, isLocalised) = EsmBinaryUtils.ForEachRecord(filePath, NpcGroup,
            (origPlugin, localFormId, recData, recMasters, loc) =>
            {
                if (loc && !stringTableLoaded)
                {
                    stringTable       = EsmStringsReader.TryLoad(filePath, bsaStringCache);
                    stringTableLoaded = true;
                }

                var entry = ParseNpcSubrecords(recData, loc, recMasters, pluginName,
                                               origPlugin, localFormId, stringTable);

                if (!string.IsNullOrEmpty(entry.EditorId) || !string.IsNullOrEmpty(entry.FullName))
                    result.Npcs.Add(entry);
            });

        result.IsLocalised        = isLocalised;
        result.Masters.AddRange(masters);
        result.StringTableEntries = stringTable?.Count ?? 0;
        return result;
    }

    // Full subrecord parser: extracts EDID, FULL (with string-table fallback for localized plugins),
    // ACBS (gender), RNAM (race), CNAM (class), KSIZ/KWDA (keywords), and FNAM (factions).
    private static NpcEntry ParseNpcSubrecords(
        byte[] data, bool isLocalised, List<string> masters, string pluginName,
        string originalPlugin, uint localFormId,
        Dictionary<uint, string>? stringTable)
    {
        var entry = new NpcEntry
        {
            OriginalPlugin = originalPlugin,
            ScanningPlugin = pluginName,
            LocalFormId    = localFormId
        };
        int pos          = 0;
        int keywordCount = 0;

        while (pos <= data.Length - 6)
        {
            var subTag  = Encoding.ASCII.GetString(data, pos, 4); pos += 4;
            var subSize = System.BitConverter.ToUInt16(data, pos); pos += 2;
            if (pos + subSize > data.Length) break;

            if (AppearanceTags.Contains(subTag)) entry.HasAppearanceData = true;

            switch (subTag)
            {
                case "EDID":
                    entry.EditorId = Encoding.ASCII.GetString(data, pos, subSize).TrimEnd('\0');
                    break;

                case "FULL":
                    if (!isLocalised && subSize > 1)
                    {
                        // Non-localized plugin: FULL is a raw null-terminated UTF-8 string.
                        entry.FullName = Encoding.UTF8.GetString(data, pos, subSize).TrimEnd('\0');
                    }
                    else if (isLocalised && subSize == 4 && stringTable != null)
                    {
                        // Localized plugin: FULL is a 4-byte string table ID.
                        uint stringId = System.BitConverter.ToUInt32(data, pos);
                        if (stringTable.TryGetValue(stringId, out var localizedName))
                            entry.FullName = localizedName;
                    }
                    break;

                case "ACBS":
                    // 24-byte Actor Base Configuration. Flags uint32 at offset 0; bit 0 = female.
                    if (subSize >= 4)
                    {
                        var acbsFlags = System.BitConverter.ToUInt32(data, pos);
                        entry.IsMale = (acbsFlags & 0x00000001) == 0;
                    }
                    break;

                case "RNAM":
                    if (subSize == 4)
                        entry.Race = ResolveFormId(data, pos, masters, pluginName);
                    break;

                case "CNAM":
                    if (subSize == 4)
                        entry.Class = ResolveFormId(data, pos, masters, pluginName);
                    break;

                case "KSIZ":
                    if (subSize == 4)
                        keywordCount = (int)System.BitConverter.ToUInt32(data, pos);
                    break;

                case "KWDA":
                {
                    int count = subSize / 4;
                    for (int k = 0; k < count && k < keywordCount; k++)
                    {
                        if (pos + k * 4 + 4 > data.Length) break;
                        entry.Keywords.Add(ResolveFormId(data, pos + k * 4, masters, pluginName));
                    }
                    break;
                }

                case "FNAM":
                {
                    // Each faction entry: FormID (4 bytes) + Rank (1 byte) = 5 bytes per entry.
                    // Some tools align to 8 bytes — handle both.
                    int entrySize = subSize > 0 && subSize % 8 == 0 ? 8
                                  : subSize > 0 && subSize % 5 == 0 ? 5
                                  : 0;
                    if (entrySize > 0)
                    {
                        for (int k = 0; k + 4 <= subSize; k += entrySize)
                        {
                            if (pos + k + 4 > data.Length) break;
                            entry.Factions.Add(ResolveFormId(data, pos + k, masters, pluginName));
                        }
                    }
                    break;
                }
            }

            pos += subSize;
        }

        return entry;
    }

    private static (string plugin, uint localId) ResolveFormId(
        byte[] data, int offset, List<string> masters, string pluginName)
    {
        var raw = System.BitConverter.ToUInt32(data, offset);
        return EsmBinaryUtils.ResolveOriginPlugin(raw, masters, pluginName);
    }
}
