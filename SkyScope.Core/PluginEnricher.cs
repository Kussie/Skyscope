using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SkyScope.Models;

namespace SkyScope.Core;

// Replaces NpcNameDatabase + FormNameDatabase.
// Per plugin: full NPC_ scan, targeted SPEL/PERK scan (only referenced plugins),
// and header-only targeted scan for base object GRUPs (only plugins with BOS refs).
public class PluginEnricher
{
    private static readonly HashSet<string> SpelPerkGroups = new() { "SPEL", "PERK" };
    private static readonly HashSet<string> ObjectGroups   = new()
        { "STAT", "FURN", "DOOR", "ACTI", "CONT", "MISC", "MSTT", "TREE" };

    private const uint FlagCompressed = 0x00040000;
    private const uint FlagDeleted    = 0x00000020;
    private const uint FlagLocalised  = 0x00000080;

    public void Enrich(ModReferenceLibrary library, string skyrimGameDirectory, IProgress<string>? progress = null)
    {
        var pluginPaths = PluginPathResolver.GetOrderedPluginPaths(skyrimGameDirectory);

        progress?.Report($"Scanning {pluginPaths.Count} plugin(s) for NPC records…");

        var npcParser  = new EsmNpcParser();
        var formParser = new EsmFormParser();
        int i = 0;

        foreach (var pluginPath in pluginPaths)
        {
            i++;
            var fileName = Path.GetFileName(pluginPath);
            progress?.Report($"[{i}/{pluginPaths.Count}]  {fileName}");

            try
            {
                // ── Full NPC scan ──────────────────────────────────────────
                var npcResult = npcParser.Parse(pluginPath);

                foreach (var npc in npcResult.Npcs)
                    library.EnrichNpc(npc.OriginalPlugin, npc.LocalFormId, npc.EditorId, npc.FullName);

                if (npcResult.Npcs.Count > 0)
                    progress?.Report($"  {fileName}: {npcResult.Npcs.Count} NPC record(s)");

                // ── Targeted SPEL/PERK scan (only for referenced plugins) ──
                if (library.SpelPerkRefPlugins.Contains(fileName))
                {
                    var spelResult = formParser.Parse(pluginPath, SpelPerkGroups);
                    foreach (var entry in spelResult.Entries)
                        library.EnrichSpellPerk(entry.OriginalPlugin, entry.LocalFormId, entry.EditorId, entry.FullName);
                }

                // ── Targeted base-object scan (only for BOS-referenced plugins) ──
                if (library.BosRefPlugins.Contains(fileName))
                    ScanForBosObjects(pluginPath, library);
            }
            catch (Exception ex)
            {
                progress?.Report($"  [warn] Skipped {fileName}: {ex.Message}");
            }
        }

        library.FinalizeNpcBareHexIndex();
        progress?.Report($"Library ready — {library.NpcRecordCount:N0} NPC record(s) indexed.");
    }

    // Targeted header-only scan for registered BOS refs across all base-object GRUPs.
    // Reads only the 20-byte record header; full data is parsed only for matching records.
    private static void ScanForBosObjects(string filePath, ModReferenceLibrary library)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (ReadTag(reader) != "TES4") return;

        var tes4DataSize = reader.ReadUInt32();
        var tes4Flags    = reader.ReadUInt32();
        reader.ReadBytes(12); // FormID(4) + Revision(4) + Version(2) + Unknown(2)
        bool isLocalised = (tes4Flags & FlagLocalised) != 0;

        var tes4End = stream.Position + tes4DataSize;
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

        var pluginName = Path.GetFileName(filePath);

        while (stream.Position < stream.Length - 23)
        {
            if (ReadTag(reader) != "GRUP") break;

            var grupSize  = reader.ReadUInt32();
            if (grupSize < 24) break;
            var label     = ReadTag(reader);
            var groupType = reader.ReadInt32();
            reader.ReadBytes(8); // Stamp(2) + Unknown(2) + Version(2) + Unknown(2)

            var contentEnd = Math.Min(stream.Position + (long)(grupSize - 24), stream.Length);

            if (ObjectGroups.Contains(label) && groupType == 0)
                ScanBosGroup(reader, stream, contentEnd, library, masters, pluginName, isLocalised);

            stream.Position = contentEnd;
        }
    }

    private static void ScanBosGroup(
        BinaryReader reader, Stream stream, long groupEnd,
        ModReferenceLibrary library, List<string> masters, string pluginName, bool isLocalised)
    {
        while (stream.Position < groupEnd - 23)
        {
            var tag = ReadTag(reader);

            if (tag == "GRUP")
            {
                var sz = reader.ReadUInt32();
                if (sz < 24) return;
                reader.ReadBytes(16);
                stream.Position = Math.Min(stream.Position + (long)(sz - 24), groupEnd);
                continue;
            }

            // Record header: dataSize(4) + flags(4) + formId(4) + revision(4) + version(2) + unknown(2)
            var dataSize = reader.ReadUInt32();
            var flags    = reader.ReadUInt32();
            var formId   = reader.ReadUInt32();
            reader.ReadBytes(8);

            var recordEnd = Math.Min(stream.Position + dataSize, groupEnd);

            if ((flags & FlagDeleted) != 0) { stream.Position = recordEnd; continue; }

            byte   masterIdx   = (byte)(formId >> 24);
            uint   localFormId = formId & 0x00FFFFFF;
            string origPlugin  = masterIdx < masters.Count ? masters[masterIdx] : pluginName;

            if (!library.HasBosRef(origPlugin, localFormId))
            {
                stream.Position = recordEnd;
                continue;
            }

            // This record is a registered BOS ref — parse EDID + FULL
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

            var (editorId, name) = ParseEdidFull(recData, isLocalised);
            library.EnrichObject(origPlugin, localFormId, editorId, name);

            stream.Position = recordEnd;
        }
    }

    private static string ReadTag(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));

    private static (string? editorId, string? name) ParseEdidFull(byte[] data, bool isLocalised)
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
                    if (!isLocalised && subSize > 1)
                        name = Encoding.UTF8.GetString(data, pos, subSize).TrimEnd('\0');
                    break;
            }

            pos += subSize;
        }

        return (editorId, name);
    }

    private static byte[] ZlibDecompress(byte[] compressed, int expectedSize)
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
