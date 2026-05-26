using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SkyScope.Core;

// Extracts NPC name and EditorID data from a Skyrim plugin file.
// Only reads the NPC_ top-level group; everything else is skipped for speed.
internal class EsmNpcParser
{
    private const uint FlagCompressed = 0x00040000;
    private const uint FlagDeleted    = 0x00000020;
    private const uint FlagLocalised  = 0x00000080; // FULL subrecords are string IDs, not inline text

    internal class NpcEntry
    {
        public string  OriginalPlugin { get; set; } = string.Empty;
        public uint    LocalFormId    { get; set; }
        public string? EditorId       { get; set; }
        public string? FullName       { get; set; }
    }

    internal class ParseResult
    {
        public List<string>   Masters     { get; } = new();
        public bool           IsLocalised { get; set; }
        public List<NpcEntry> Npcs        { get; } = new();
    }

    public ParseResult Parse(string filePath)
    {
        var result = new ParseResult();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (ReadTag(reader) != "TES4") return result;

        var tes4DataSize = reader.ReadUInt32();
        var tes4Flags    = reader.ReadUInt32();
        reader.ReadBytes(12); // FormID(4) + Revision(4) + Version(2) + Unknown(2)
        result.IsLocalised = (tes4Flags & FlagLocalised) != 0;

        var tes4End = stream.Position + tes4DataSize;
        while (stream.Position < tes4End - 5)
        {
            var sub  = ReadTag(reader);
            var size = reader.ReadUInt16();
            if (sub == "MAST")
                result.Masters.Add(Encoding.ASCII.GetString(reader.ReadBytes(size)).TrimEnd('\0'));
            else
                stream.Seek(size, SeekOrigin.Current);
        }
        stream.Position = tes4End;

        while (stream.Position < stream.Length - 23)
        {
            if (ReadTag(reader) != "GRUP") break;

            var grupSize = reader.ReadUInt32();
            if (grupSize < 24) break;

            var label     = ReadTag(reader);
            var groupType = reader.ReadInt32();
            reader.ReadBytes(8); // Stamp(2) + Unknown(2) + Version(2) + Unknown(2)

            var contentEnd = Math.Min(stream.Position + (long)(grupSize - 24), stream.Length);

            if (label == "NPC_" && groupType == 0)
                ParseNpcGroup(reader, stream, contentEnd, result, Path.GetFileName(filePath));

            stream.Position = contentEnd;
        }

        return result;
    }

    private void ParseNpcGroup(BinaryReader reader, Stream stream, long groupEnd,
                                ParseResult result, string pluginName)
    {
        while (stream.Position < groupEnd - 23)
        {
            var tag = ReadTag(reader);

            if (tag == "GRUP")
            {
                var size = reader.ReadUInt32();
                if (size < 24) return;
                reader.ReadBytes(16);
                stream.Position = Math.Min(stream.Position + (long)(size - 24), groupEnd);
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

            var recordEnd = Math.Min(stream.Position + dataSize, groupEnd);

            if ((flags & FlagDeleted) != 0)
            {
                stream.Position = recordEnd;
                continue;
            }

            byte   masterIdx  = (byte)(formId >> 24);
            uint   localFormId = formId & 0x00FFFFFF;
            string origPlugin  = masterIdx < result.Masters.Count
                ? result.Masters[masterIdx]
                : pluginName;

            byte[] recData;
            try
            {
                if ((flags & FlagCompressed) != 0)
                {
                    var uncompSize = reader.ReadUInt32();
                    var compSize   = (int)(recordEnd - stream.Position);
                    if (compSize <= 0) { stream.Position = recordEnd; continue; }
                    recData = ZlibDecompress(reader.ReadBytes(compSize), (int)uncompSize);
                }
                else
                {
                    recData = reader.ReadBytes((int)(recordEnd - stream.Position));
                }
            }
            catch { stream.Position = recordEnd; continue; }

            var entry = ParseSubrecords(recData, result.IsLocalised);
            entry.OriginalPlugin = origPlugin;
            entry.LocalFormId    = localFormId;

            if (!string.IsNullOrEmpty(entry.EditorId) || !string.IsNullOrEmpty(entry.FullName))
                result.Npcs.Add(entry);

            stream.Position = recordEnd;
        }
    }

    private static NpcEntry ParseSubrecords(byte[] data, bool isLocalised)
    {
        var entry = new NpcEntry();
        int pos   = 0;

        while (pos <= data.Length - 6)
        {
            var subTag  = Encoding.ASCII.GetString(data, pos, 4); pos += 4;
            var subSize = BitConverter.ToUInt16(data, pos);        pos += 2;

            if (pos + subSize > data.Length) break;

            switch (subTag)
            {
                case "EDID":
                    entry.EditorId = Encoding.ASCII.GetString(data, pos, subSize).TrimEnd('\0');
                    break;
                case "FULL":
                    // Localised plugins store a 4-byte string ID here, not inline text.
                    // Without .strings files we can't resolve it, so skip and fall back to EditorId.
                    if (!isLocalised && subSize > 1)
                        entry.FullName = Encoding.UTF8.GetString(data, pos, subSize).TrimEnd('\0');
                    break;
            }

            pos += subSize;
        }

        return entry;
    }

    private static void SkipRecord(BinaryReader reader, Stream stream, long groupEnd)
    {
        if (stream.Position + 20 > groupEnd) { stream.Position = groupEnd; return; }
        var dataSize = reader.ReadUInt32();
        reader.ReadBytes(16); // Flags(4) + FormID(4) + Revision(4) + Version(2) + Unknown(2)
        stream.Position = Math.Min(stream.Position + dataSize, groupEnd);
    }

    private static string ReadTag(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));

    private static byte[] ZlibDecompress(byte[] compressed, int expectedSize)
    {
        using var ms = new MemoryStream(compressed);
        // Bethesda uses standard zlib (RFC 1950) — skip the 2-byte header (CMF + FLG) and feed
        // the raw deflate stream directly. ZLibStream has compatibility issues with some
        // Bethesda-produced streams; DeflateStream is reliable.
        ms.ReadByte();
        ms.ReadByte();
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        var output = new byte[expectedSize];
        int read = 0;
        while (read < expectedSize)
        {
            int n = deflate.Read(output, read, expectedSize - read);
            if (n == 0) break;
            read += n;
        }
        return output;
    }
}
