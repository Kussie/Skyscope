using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SkyScope.Core;

public class NpcNameDatabase
{
    // (plugin.ToLower(), localFormId) -> (fullName, editorId)
    private readonly Dictionary<(string, uint), (string? Name, string? EditorId)> _lookup = new();

    // fullName (case-insensitive) -> editorId — for resolving SPID StringFilter targets
    private readonly Dictionary<string, string> _nameToEditorId = new(StringComparer.OrdinalIgnoreCase);

    // all known NPC editorIds — used to reject SPID FormFilter values that are factions/keywords
    private readonly HashSet<string> _editorIdSet = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded    { get; private set; }
    public int  RecordCount => _lookup.Count;

    public Dictionary<string, int> NpcCountByPlugin { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Load(string skyrimGameDirectory, IProgress<string>? progress = null)
    {
        _lookup.Clear();
        _nameToEditorId.Clear();
        _editorIdSet.Clear();
        NpcCountByPlugin.Clear();
        IsLoaded = false;

        var dataDir     = Path.Combine(skyrimGameDirectory, "Data");
        var pluginPaths = GetOrderedPluginPaths(skyrimGameDirectory, dataDir);

        progress?.Report($"Scanning {pluginPaths.Count} plugin(s) for NPC records…");

        var parser = new EsmNpcParser();
        int i = 0;

        foreach (var pluginPath in pluginPaths)
        {
            i++;
            var fileName = Path.GetFileName(pluginPath);
            progress?.Report($"[{i}/{pluginPaths.Count}]  {fileName}");

            try
            {
                var parsed = parser.Parse(pluginPath);
                int added  = 0;

                foreach (var npc in parsed.Npcs)
                {
                    var key = (npc.OriginalPlugin.ToLowerInvariant(), npc.LocalFormId);

                    // Later plugins win, but only for non-null values. Skyrim override records omit
                    // unchanged subrecords, so a missing FULL/EDID means "unchanged", not "clear it".
                    if (_lookup.TryGetValue(key, out var existing))
                    {
                        _lookup[key] = (
                            string.IsNullOrEmpty(npc.FullName)  ? existing.Name     : npc.FullName,
                            string.IsNullOrEmpty(npc.EditorId)  ? existing.EditorId : npc.EditorId
                        );
                    }
                    else
                    {
                        _lookup[key] = (npc.FullName, npc.EditorId);
                        added++;
                    }

                    if (!string.IsNullOrEmpty(npc.FullName) && !string.IsNullOrEmpty(npc.EditorId))
                        _nameToEditorId[npc.FullName] = npc.EditorId;

                    if (!string.IsNullOrEmpty(npc.EditorId))
                        _editorIdSet.Add(npc.EditorId);
                }

                NpcCountByPlugin[fileName] = parsed.Npcs.Count;
                if (parsed.Npcs.Count > 0)
                    progress?.Report($"  {fileName}: {parsed.Npcs.Count} NPC record(s) ({added} new)");
            }
            catch (Exception ex)
            {
                progress?.Report($"  [warn] Skipped {fileName}: {ex.Message}");
            }
        }

        IsLoaded = true;
        progress?.Report($"NPC database ready — {_lookup.Count:N0} record(s) indexed.");
    }

    public string? ResolveName(string plugin, string formIdHex)
    {
        var (name, editorId) = Lookup(plugin, formIdHex);
        return name ?? editorId;
    }

    public string? ResolveEditorId(string plugin, string formIdHex)
    {
        var (_, editorId) = Lookup(plugin, formIdHex);
        return editorId;
    }

    public string? FindEditorIdByName(string name) =>
        _nameToEditorId.TryGetValue(name, out var editorId) ? editorId : null;

    public bool IsNpcEditorId(string editorId) => _editorIdSet.Contains(editorId);

    private (string? Name, string? EditorId) Lookup(string plugin, string formIdHex)
    {
        if (!uint.TryParse(formIdHex, NumberStyles.HexNumber, null, out var formId))
            return (null, null);

        uint localFormId = formId & 0x00FFFFFF;
        var  key         = (plugin.ToLowerInvariant(), localFormId);

        return _lookup.TryGetValue(key, out var entry) ? entry : (null, null);
    }

    private static List<string> GetOrderedPluginPaths(string skyrimGameDirectory, string dataDir)
    {
        if (!Directory.Exists(dataDir)) return new List<string>();

        var pluginsTxt = FindPluginsTxt(skyrimGameDirectory);
        if (pluginsTxt != null)
        {
            var ordered = new List<string>();
            foreach (var line in File.ReadAllLines(pluginsTxt))
            {
                // Active plugins are prefixed with '*' in SE; LE lists all active plugins without prefix
                var trimmed = line.TrimStart('*').Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

                var fullPath = Path.Combine(dataDir, trimmed);
                if (File.Exists(fullPath))
                    ordered.Add(fullPath);
            }

            // Mod managers (MO2, Vortex) treat core masters as implicit and may omit them from
            // plugins.txt. Scan Data/*.esm and prepend any that aren't already listed.
            var listed = new HashSet<string>(
                ordered.Select(p => Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            var missingEsms = Directory.GetFiles(dataDir, "*.esm")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Where(f => !listed.Contains(Path.GetFileName(f)))
                .ToList();

            ordered.InsertRange(0, missingEsms);

            if (ordered.Count > 0) return ordered;
        }

        // Fallback: ESMs first (alphabetical), then ESLs, then ESPs
        return Directory.GetFiles(dataDir, "*.esm").OrderBy(f => f)
            .Concat(Directory.GetFiles(dataDir, "*.esl").OrderBy(f => f))
            .Concat(Directory.GetFiles(dataDir, "*.esp").OrderBy(f => f))
            .ToList();
    }

    private static string? FindPluginsTxt(string skyrimGameDirectory)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var se = Path.Combine(local, "Skyrim Special Edition", "plugins.txt");
        if (File.Exists(se)) return se;

        var vr = Path.Combine(local, "Skyrim VR", "plugins.txt");
        if (File.Exists(vr)) return vr;

        var le = Path.Combine(local, "Skyrim", "plugins.txt");
        if (File.Exists(le)) return le;

        // Some mod managers write plugins.txt next to the game executable
        var gameSide = Path.Combine(skyrimGameDirectory, "plugins.txt");
        if (File.Exists(gameSide)) return gameSide;

        return null;
    }
}
