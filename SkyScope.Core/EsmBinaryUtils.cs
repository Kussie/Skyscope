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
