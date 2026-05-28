using System;
using System.Collections.Generic;
using System.IO;
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
                {
                    NpcAttributeSet? attrs = null;
                    if (npc.Race.HasValue || npc.Class.HasValue || npc.Keywords.Count > 0
                        || npc.Factions.Count > 0 || npc.IsMale.HasValue)
                    {
                        attrs = new NpcAttributeSet
                        {
                            Race  = npc.Race,
                            Class = npc.Class,
                            IsMale = npc.IsMale
                        };
                        attrs.Keywords.AddRange(npc.Keywords);
                        attrs.Factions.AddRange(npc.Factions);
                    }
                    library.EnrichNpc(npc.OriginalPlugin, npc.LocalFormId, npc.EditorId, npc.FullName, attrs);
                }

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

        var (valid, masters, isLocalised) = EsmBinaryUtils.ReadPluginHeader(reader, stream);
        if (!valid) return;

        var pluginName = Path.GetFileName(filePath);

        while (stream.Position < stream.Length - 23)
        {
            if (EsmBinaryUtils.ReadTag(reader) != "GRUP") break;

            var grupSize  = reader.ReadUInt32();
            if (grupSize < 24) break;
            var label     = EsmBinaryUtils.ReadTag(reader);
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
            var tag = EsmBinaryUtils.ReadTag(reader);

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

            if ((flags & EsmBinaryUtils.FlagDeleted) != 0) { stream.Position = recordEnd; continue; }

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

            var (editorId, name) = EsmBinaryUtils.ParseEdidFull(recData, isLocalised);
            library.EnrichObject(origPlugin, localFormId, editorId, name);

            stream.Position = recordEnd;
        }
    }
}
