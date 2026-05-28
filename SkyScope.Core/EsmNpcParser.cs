using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyScope.Core;

// Extracts NPC name and EditorID data from a Skyrim plugin file.
// Only reads the NPC_ top-level group; everything else is skipped for speed.
internal class EsmNpcParser
{
    internal class NpcEntry
    {
        public string  OriginalPlugin { get; set; } = string.Empty;
        public uint    LocalFormId    { get; set; }
        public string? EditorId       { get; set; }
        public string? FullName       { get; set; }
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
            reader.ReadBytes(8); // Stamp(2) + Unknown(2) + Version(2) + Unknown(2)

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
            reader.ReadBytes(8); // Revision(4) + Version(2) + Unknown(2)

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
            catch { stream.Position = recordEnd; continue; } // skip malformed/truncated records

            var (editorId, fullName) = EsmBinaryUtils.ParseEdidFull(recData, result.IsLocalised);

            if (!string.IsNullOrEmpty(editorId) || !string.IsNullOrEmpty(fullName))
                result.Npcs.Add(new NpcEntry
                {
                    OriginalPlugin = origPlugin,
                    LocalFormId    = localFormId,
                    EditorId       = editorId,
                    FullName       = fullName
                });

            stream.Position = recordEnd;
        }
    }

    private static void SkipRecord(BinaryReader reader, Stream stream, long groupEnd)
    {
        if (stream.Position + 20 > groupEnd) { stream.Position = groupEnd; return; }
        var dataSize = reader.ReadUInt32();
        reader.ReadBytes(16); // Flags(4) + FormID(4) + Revision(4) + Version(2) + Unknown(2)
        stream.Position = System.Math.Min(stream.Position + dataSize, groupEnd);
    }
}
