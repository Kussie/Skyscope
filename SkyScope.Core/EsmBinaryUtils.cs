using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SkyScope.Core;

// Shared low-level helpers for reading Bethesda plugin files (.esm/.esp/.esl).
// All methods are stateless and operate on caller-supplied streams/readers.
internal static class EsmBinaryUtils
{
    internal const uint FlagCompressed = 0x00040000;
    internal const uint FlagDeleted    = 0x00000020;
    internal const uint FlagLocalised  = 0x00000080;

    internal static string ReadTag(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));

    // Reads the TES4 plugin header, collects the master list, and advances the stream
    // to the first top-level GRUP.  Returns (false, …) when the file is not a valid plugin.
    internal static (bool Valid, List<string> Masters, bool IsLocalised) ReadPluginHeader(
        BinaryReader reader, Stream stream)
    {
        if (ReadTag(reader) != "TES4") return (false, [], false);

        var dataSize     = reader.ReadUInt32();
        var flags        = reader.ReadUInt32();
        reader.ReadBytes(12); // FormID(4) + Revision(4) + Version(2) + Unknown(2)
        bool isLocalised = (flags & FlagLocalised) != 0;

        var tes4End = stream.Position + dataSize;
        var masters = new List<string>();
        while (stream.Position < tes4End - 5)
        {
            var sub  = ReadTag(reader);
            var size = reader.ReadUInt16();
            if (sub == "MAST")
                masters.Add(Encoding.ASCII.GetString(reader.ReadBytes(size)).TrimEnd('\0'));
            else
                stream.Seek(size, SeekOrigin.Current);
        }
        stream.Position = tes4End;

        return (true, masters, isLocalised);
    }

    // Invoked for each live (non-deleted) record inside a target top-level GRUP.
    // recData is already decompressed; localFormId is masked to 24 bits.
    internal delegate void RecordCallback(
        string originalPlugin, uint localFormId, byte[] recData, List<string> masters, bool isLocalised);

    // Opens a plugin, reads its header, and walks every record inside the requested top-level
    // GRUP labels, invoking handler for each live record. Centralises the GRUP/record/decompress
    // walk shared by the NPC and SPEL/PERK parsers. Returns the header info for the caller.
    internal static (bool Valid, List<string> Masters, bool IsLocalised) ForEachRecord(
        string filePath, HashSet<string> targetGroups, RecordCallback handler)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var (valid, masters, isLocalised) = ReadPluginHeader(reader, stream);
        if (!valid) return (false, masters, isLocalised);

        var pluginName = Path.GetFileName(filePath);

        while (stream.Position < stream.Length - 23)
        {
            if (ReadTag(reader) != "GRUP") break;

            var grupSize = reader.ReadUInt32();
            if (grupSize < 24) break;

            var label     = ReadTag(reader);
            var groupType = reader.ReadInt32();
            reader.ReadBytes(8); // Stamp(2) + Unknown(2) + Version(2) + Unknown(2)

            var contentEnd = Math.Min(stream.Position + (long)(grupSize - 24), stream.Length);

            if (targetGroups.Contains(label) && groupType == 0)
                WalkGroup(reader, stream, contentEnd, masters, pluginName, label, isLocalised, handler);

            stream.Position = contentEnd;
        }

        return (true, masters, isLocalised);
    }

    private static void WalkGroup(
        BinaryReader reader, Stream stream, long groupEnd, List<string> masters,
        string pluginName, string expectedTag, bool isLocalised, RecordCallback handler)
    {
        while (stream.Position < groupEnd - 23)
        {
            var tag = ReadTag(reader);

            // Nested GRUP (e.g. cell/world children) — skip wholesale.
            if (tag == "GRUP")
            {
                var size = reader.ReadUInt32();
                if (size < 24) return;
                reader.ReadBytes(16);
                stream.Position = Math.Min(stream.Position + (long)(size - 24), groupEnd);
                continue;
            }

            var dataSize = reader.ReadUInt32();
            var flags    = reader.ReadUInt32();
            var formId   = reader.ReadUInt32();
            reader.ReadBytes(8); // Revision(4) + Version(2) + Unknown(2)

            var recordEnd = Math.Min(stream.Position + dataSize, groupEnd);

            // Only hand back records of the expected type; skip others (and deleted records).
            if (tag != expectedTag || (flags & FlagDeleted) != 0)
            {
                stream.Position = recordEnd;
                continue;
            }

            var (origPlugin, localFormId) = ResolveOriginPlugin(formId, masters, pluginName);

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
            catch { stream.Position = recordEnd; continue; } // skip malformed/truncated records

            handler(origPlugin, localFormId, recData, masters, isLocalised);
            stream.Position = recordEnd;
        }
    }

    // Maps a raw record FormID to its originating plugin and 24-bit local FormID.
    // A new record's master byte equals the master count (the plugin implicitly trails its own
    // master list); override records index into the master list.
    // NOTE: light-master (ESL, master byte 0xFE) override references cannot be attributed to the
    // exact light master without the global light-master order, so they fall back to the current
    // plugin. Base records defined in an ESL are still indexed correctly (their master byte equals
    // the master count), so name resolution by Plugin|FormId is unaffected.
    internal static (string Plugin, uint LocalFormId) ResolveOriginPlugin(
        uint formId, List<string> masters, string pluginName)
    {
        byte masterIdx   = (byte)(formId >> 24);
        uint localFormId = formId & 0x00FFFFFF;
        string plugin    = masterIdx < masters.Count ? masters[masterIdx] : pluginName;
        return (plugin, localFormId);
    }

    // Parses EDID and FULL subrecords from an already-decompressed record byte array.
    internal static (string? EditorId, string? Name) ParseEdidFull(byte[] data, bool isLocalised)
    {
        string? editorId = null;
        string? name     = null;
        int pos = 0;

        while (pos <= data.Length - 6)
        {
            var subTag  = Encoding.ASCII.GetString(data, pos, 4); pos += 4;
            var subSize = BitConverter.ToUInt16(data, pos);        pos += 2;
            if (pos + subSize > data.Length) break;

            switch (subTag)
            {
                case "EDID":
                    editorId = Encoding.ASCII.GetString(data, pos, subSize).TrimEnd('\0');
                    break;
                case "FULL":
                    // Localised plugins store a 4-byte string table ID here; skip without .strings files.
                    if (!isLocalised && subSize > 1)
                        name = Encoding.UTF8.GetString(data, pos, subSize).TrimEnd('\0');
                    break;
            }

            pos += subSize;
        }

        return (editorId, name);
    }

    // Bethesda uses standard zlib (RFC 1950) — skip the 2-byte header (CMF + FLG) and feed
    // the raw deflate stream directly.  ZLibStream has compatibility issues with some
    // Bethesda-produced streams; DeflateStream is reliable.
    internal static byte[] ZlibDecompress(byte[] compressed, int expectedSize)
    {
        using var ms = new MemoryStream(compressed);
        ms.ReadByte();
        ms.ReadByte();
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        var output = new byte[expectedSize];
        int read   = 0;
        while (read < expectedSize)
        {
            int n = deflate.Read(output, read, expectedSize - read);
            if (n == 0) break;
            read += n;
        }
        return output;
    }
}
