using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;

namespace SkyScope.Core;

// Scans BSA archives in the Data directory to extract NPC name string tables.
// Vanilla Skyrim SE ships localised strings packed in BSAs rather than loose files.
internal static class BsaStringExtractor
{
    private const uint BsaMagic   = 0x00415342; // "BSA\0"
    private const uint Version104 = 104;
    private const uint Version105 = 105;

    // ── Main extraction ──────────────────────────────────────────────────────

    // Returns: { pluginName (lowercase) → { stringId → displayName } }
    internal static Dictionary<string, Dictionary<uint, string>> ExtractStringTables(string dataDir)
    {
        var result = new Dictionary<string, Dictionary<uint, string>>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(dataDir)) return result;

        foreach (var bsaPath in Directory.GetFiles(dataDir, "*.bsa")
                                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            try { ScanBsa(bsaPath, result); }
            catch { }
        }

        return result;
    }

    // ── Core BSA scan ────────────────────────────────────────────────────────

    private static void ScanBsa(
        string bsaPath,
        Dictionary<string, Dictionary<uint, string>> result)
    {
        using var stream = new FileStream(bsaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r      = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (stream.Length < 36) return;

        // Header
        if (r.ReadUInt32() != BsaMagic) return;
        var version = r.ReadUInt32();
        if (version != Version104 && version != Version105) return;

        r.ReadUInt32();                           // folder records offset (always 36)
        var archiveFlags = r.ReadUInt32();
        var folderCount  = r.ReadUInt32();
        var fileCount    = r.ReadUInt32();
        var totalFolderNameLength = r.ReadUInt32();
        var totalFileNameLength   = r.ReadUInt32();
        r.ReadUInt32();                           // content type flags

        bool hasDirectoryNames = (archiveFlags & 0x01) != 0;
        bool embeddedNames     = (archiveFlags & 0x800) != 0;
        bool defaultCompressed = (archiveFlags & 0x04) != 0;
        int  frSize            = version == Version105 ? 24 : 16;

        // Folder records
        var folderOffsets    = new long[folderCount];
        var folderFileCounts = new uint[folderCount];

        stream.Position = 36;
        for (int i = 0; i < folderCount; i++)
        {
            r.ReadUInt64();
            folderFileCounts[i] = r.ReadUInt32();

            if (version == Version105)
            {
                r.ReadUInt32();
                // SSE stores a uint64 offset; like v104 it has totalFileNameLength added.
                folderOffsets[i] = (long)r.ReadUInt64() - totalFileNameLength;
            }
            else
            {
                // v104: stored offset has totalFileNameLength added
                folderOffsets[i] = (long)r.ReadUInt32() - totalFileNameLength;
            }
        }

        // Folder data blocks
        var records = new List<(string folder, uint sizeFlags, uint dataOffset)>((int)fileCount);

        for (int i = 0; i < (int)folderCount; i++)
        {
            if (folderOffsets[i] < 36 || folderOffsets[i] >= stream.Length) continue;
            stream.Position = folderOffsets[i];

            string folder = "";
            if (hasDirectoryNames)
            {
                byte nameLen = r.ReadByte();
                folder = Encoding.UTF8.GetString(r.ReadBytes(nameLen))
                             .TrimEnd('\0').ToLowerInvariant().Replace('\\', '/');
            }

            for (int j = 0; j < (int)folderFileCounts[i]; j++)
            {
                r.ReadUInt64();
                uint sf  = r.ReadUInt32();
                uint ofs = r.ReadUInt32();
                records.Add((folder, sf, ofs));
            }
        }

        // Early-out: most archives (textures, meshes, sounds, voices) have no "strings" folder.
        // Skip reading the (potentially multi-MB) file-name block for those entirely.
        bool hasStringsFolder = false;
        foreach (var rec in records)
            if (rec.folder == "strings") { hasStringsFolder = true; break; }
        if (!hasStringsFolder) return;

        // File names block
        long fileNamesStart = 36L
            + (long)folderCount * frSize
            + totalFolderNameLength
            + (long)fileCount * 16L;

        if (fileNamesStart >= stream.Length) return;
        stream.Position = fileNamesStart;

        var names = new string[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            var bytes = new List<byte>(32);
            byte b;
            while (stream.Position < stream.Length && (b = r.ReadByte()) != 0)
                bytes.Add(b);
            names[i] = Encoding.UTF8.GetString(bytes.ToArray()).ToLowerInvariant();
        }

        // Extract matching strings files
        for (int i = 0; i < records.Count; i++)
        {
            var (folder, sizeFlags, dataOfs) = records[i];
            var name = names[i];

            if (folder != "strings") continue;
            if (!name.EndsWith(".strings")) continue;

            bool isEnglish = name.Contains("_english.") || name.Contains("_en.");
            if (!isEnglish) continue;

            int sep = name.LastIndexOf('_');
            if (sep <= 0) continue;
            string pluginName = name[..sep];

            if (result.ContainsKey(pluginName)) continue;

            bool overrideBit    = (sizeFlags & (1u << 30)) != 0;
            bool fileCompressed = defaultCompressed ^ overrideBit;
            int  fileSize       = (int)(sizeFlags & 0x3FFF_FFFF);

            if (fileSize < 8 || dataOfs >= stream.Length) continue;

            stream.Position = dataOfs;

            // v105 embedded file name prefix: 1-byte length + name bytes (no null)
            if (embeddedNames && fileSize > 1)
            {
                int embLen = r.ReadByte();
                if (embLen >= fileSize) continue;
                stream.Position += embLen;
                fileSize -= 1 + embLen;
            }

            if (fileSize < 8 || stream.Position + fileSize > stream.Length) continue;

            var raw = r.ReadBytes(fileSize);

            byte[] data;
            if (fileCompressed)
            {
                data = version == Version105
                    ? DecompressLZ4(raw)
                    : DecompressZlib(raw);
            }
            else
            {
                data = raw;
            }

            if (data.Length == 0) continue;

            var table = EsmStringsReader.ParseData(data);
            if (table.Count > 0)
                result[pluginName] = table;
        }
    }

    // ── Decompression helpers ────────────────────────────────────────────────

    // BSA v105 (SSE): LZ4 block format.
    // First 4 bytes = original uncompressed size (uint32 LE); remainder = LZ4 block data.
    private static byte[] DecompressLZ4(byte[] compressed)
    {
        try
        {
            if (compressed.Length < 5) return [];
            int uncompressedSize = BitConverter.ToInt32(compressed, 0);
            if (uncompressedSize <= 0 || uncompressedSize > 64 * 1024 * 1024) return [];

            var target  = new byte[uncompressedSize];
            int decoded = LZ4Codec.Decode(compressed, 4, compressed.Length - 4,
                                          target,     0, uncompressedSize);
            return decoded > 0 ? target : [];
        }
        catch { return []; }
    }

    // BSA v104 (LE): standard zlib (skip 2-byte CMF+FLG header).
    private static byte[] DecompressZlib(byte[] compressed)
    {
        try
        {
            using var ms      = new MemoryStream(compressed, 2, compressed.Length - 2);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var output  = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch { return []; }
    }
}
