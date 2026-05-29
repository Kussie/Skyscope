using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyScope.Core;

// Reads Bethesda .STRINGS files for localized plugins.
// Localized FULL subrecords store a 4-byte string table ID; this class resolves those IDs
// to their actual display names.  Loose files are checked first; BSA-packed tables are
// supplied via the optional bsaCache parameter in TryLoad.
internal static class EsmStringsReader
{
    private static readonly string[] LanguageCandidates =
    [
        "English", "en",
        "French", "German", "Spanish", "Italian",
        "Russian", "Polish", "Czech", "Portuguese",
        "Japanese", "Chinese", "Korean"
    ];

    // Returns the string table for a plugin, trying loose files first then the BSA cache.
    internal static Dictionary<uint, string>? TryLoad(
        string pluginPath,
        Dictionary<string, Dictionary<uint, string>>? bsaCache = null)
    {
        var dataDir = Path.GetDirectoryName(pluginPath);
        var pluginName = Path.GetFileNameWithoutExtension(pluginPath);

        // ── 1. Loose files ───────────────────────────────────────────────────
        if (dataDir != null)
        {
            var stringsDir = Path.Combine(dataDir, "Strings");
            if (Directory.Exists(stringsDir))
            {
                foreach (var lang in LanguageCandidates)
                {
                    var path = Path.Combine(stringsDir, $"{pluginName}_{lang}.STRINGS");
                    if (File.Exists(path))
                        return LoadFile(path);
                }

                try
                {
                    foreach (var file in Directory.GetFiles(stringsDir, $"{pluginName}_*.STRINGS"))
                        return LoadFile(file);
                }
                catch { }
            }
        }

        // ── 2. BSA cache fallback ────────────────────────────────────────────
        if (bsaCache != null && pluginName != null &&
            bsaCache.TryGetValue(pluginName, out var cached))
            return cached;

        return null;
    }

    private static Dictionary<uint, string> LoadFile(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return ParseData(data);
        }
        catch { return []; }
    }

    // Parses raw .STRINGS bytes.  Exposed internally so BsaStringExtractor can reuse it.
    internal static Dictionary<uint, string> ParseData(byte[] data)
    {
        var result = new Dictionary<uint, string>();
        try
        {
            if (data.Length < 8) return result;

            uint count    = BitConverter.ToUInt32(data, 0);
            uint dataSize = BitConverter.ToUInt32(data, 4);

            if (count > 500_000 || dataSize > 64 * 1024 * 1024) return result;

            // Directory: count × interleaved (uint32 id, uint32 offset) pairs
            int dirBytes = (int)count * 8;
            if (8 + dirBytes + dataSize > data.Length) return result;

            int stringsBase = 8 + dirBytes;

            for (uint i = 0; i < count; i++)
            {
                int pos    = 8 + (int)i * 8;
                uint id    = BitConverter.ToUInt32(data, pos);
                uint ofs   = BitConverter.ToUInt32(data, pos + 4);
                int absOfs = stringsBase + (int)ofs;

                if (absOfs >= data.Length) continue;

                int end = Array.IndexOf(data, (byte)0, absOfs);
                if (end < 0) end = data.Length;

                var str = Encoding.UTF8.GetString(data, absOfs, end - absOfs).Trim();
                if (!string.IsNullOrEmpty(str))
                    result[id] = str;
            }
        }
        catch { }

        return result;
    }
}
