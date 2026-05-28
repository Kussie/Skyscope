using System.Collections.Generic;
using System.IO;
using System.Text;
using SkyScope.Models;

namespace SkyScope.Core;

// Extracts NPC data (name, EditorId, race, class, factions, keywords, gender) from a plugin file.
// Only reads the NPC_ top-level group; everything else is skipped for speed.
internal class EsmNpcParser
{
    internal class NpcEntry
    {
        public string  OriginalPlugin { get; set; } = string.Empty;
        public uint    LocalFormId    { get; set; }
        public string? EditorId       { get; set; }
        public string? FullName       { get; set; }

        // Attributes from binary subrecords — resolved to (originalPlugin, localFormId) tuples
        public (string plugin, uint localId)? Race    { get; set; }
        public (string plugin, uint localId)? Class   { get; set; }
        public List<(string plugin, uint localId)> Keywords { get; set; } = [];
        public List<(string plugin, uint localId)> Factions { get; set; } = [];
        public bool? IsMale { get; set; }
    }

    internal class ParseResult
    {
        public List<string>   Masters     { get; } = [];
        public bool           IsLocalised { get; set; }
        public List<NpcEntry> Npcs        { get; } = [];
    }

    public ParseResult Parse(string filePath)
    {
        var result = new ParseResult();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var (valid, masters, isLocalised) = EsmBinaryUtils.ReadPluginHeader(reader, stream);
        if (!valid) return result;

        result.IsLocalised = isLocalised;
        result.Masters.AddRange(masters);

        while (stream.Position < stream.Length - 23)
        {
            if (EsmBinaryUtils.ReadTag(reader) != "GRUP") break;

            var grupSize = reader.ReadUInt32();
            if (grupSize < 24) break;

            var label     = EsmBinaryUtils.ReadTag(reader);
            var groupType = reader.ReadInt32();
            reader.ReadBytes(8);

            var contentEnd = System.Math.Min(stream.Position + (long)(grupSize - 24), stream.Length);

            if (label == "NPC_" && groupType == 0)
                ParseNpcGroup(reader, stream, contentEnd, result, Path.GetFileName(filePath));

            stream.Position = contentEnd;
        }

        return result;
    }

    private static void ParseNpcGroup(BinaryReader reader, Stream stream, long groupEnd,
                                      ParseResult result, string pluginName)
    {
        while (stream.Position < groupEnd - 23)
        {
            var tag = EsmBinaryUtils.ReadTag(reader);

            if (tag == "GRUP")
            {
                var size = reader.ReadUInt32();
                if (size < 24) return;
                reader.ReadBytes(16);
                stream.Position = System.Math.Min(stream.Position + (long)(size - 24), groupEnd);
                continue;
            }

            if (tag != "NPC_")
            {
                SkipRecord(reader, stream, groupEnd);
                continue;
            }

            var dataSize = reader.ReadUInt32();
            var flags    = reader.ReadUInt32();
            var formId   = reader.ReadUInt32();
            reader.ReadBytes(8);

            var recordEnd = System.Math.Min(stream.Position + dataSize, groupEnd);

            if ((flags & EsmBinaryUtils.FlagDeleted) != 0)
            {
                stream.Position = recordEnd;
                continue;
            }

            byte   masterIdx   = (byte)(formId >> 24);
            uint   localFormId = formId & 0x00FFFFFF;
            string origPlugin  = masterIdx < result.Masters.Count
                ? result.Masters[masterIdx]
                : pluginName;

            byte[] recData;
            try
            {
                if ((flags & EsmBinaryUtils.FlagCompressed) != 0)
                {
                    var uncompSize = reader.ReadUInt32();
                    var compSize   = (int)(recordEnd - stream.Position);
                    if (compSize <= 0) { stream.Position = recordEnd; continue; }
                    recData = EsmBinaryUtils.ZlibDecompress(reader.ReadBytes(compSize), (int)uncompSize);
                }
                else
                {
                    recData = reader.ReadBytes((int)(recordEnd - stream.Position));
                }
            }
            catch { stream.Position = recordEnd; continue; }

            var entry = ParseNpcSubrecords(recData, result.IsLocalised, result.Masters, pluginName,
                                           origPlugin, localFormId);

            if (!string.IsNullOrEmpty(entry.EditorId) || !string.IsNullOrEmpty(entry.FullName))
                result.Npcs.Add(entry);

            stream.Position = recordEnd;
        }
    }

    // Full subrecord parser: extracts EDID, FULL, ACBS (gender), RNAM (race), CNAM (class),
    // KSIZ/KWDA (keywords), and FNAM (factions) in a single pass.
    private static NpcEntry ParseNpcSubrecords(
        byte[] data, bool isLocalised, List<string> masters, string pluginName,
        string originalPlugin, uint localFormId)
    {
        var entry = new NpcEntry { OriginalPlugin = originalPlugin, LocalFormId = localFormId };
        int pos          = 0;
        int keywordCount = 0;

        while (pos <= data.Length - 6)
        {
            var subTag  = Encoding.ASCII.GetString(data, pos, 4); pos += 4;
            var subSize = System.BitConverter.ToUInt16(data, pos); pos += 2;
            if (pos + subSize > data.Length) break;

            switch (subTag)
            {
                case "EDID":
                    entry.EditorId = Encoding.ASCII.GetString(data, pos, subSize).TrimEnd('\0');
                    break;

                case "FULL":
                    if (!isLocalised && subSize > 1)
                        entry.FullName = Encoding.UTF8.GetString(data, pos, subSize).TrimEnd('\0');
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
        var   raw        = System.BitConverter.ToUInt32(data, offset);
        byte  masterByte = (byte)(raw >> 24);
        uint  localId    = raw & 0x00FFFFFF;
        string plugin    = masterByte < masters.Count ? masters[masterByte] : pluginName;
        return (plugin, localId);
    }

    private static void SkipRecord(BinaryReader reader, Stream stream, long groupEnd)
    {
        if (stream.Position + 20 > groupEnd) { stream.Position = groupEnd; return; }
        var dataSize = reader.ReadUInt32();
        reader.ReadBytes(16);
        stream.Position = System.Math.Min(stream.Position + dataSize, groupEnd);
    }
}
