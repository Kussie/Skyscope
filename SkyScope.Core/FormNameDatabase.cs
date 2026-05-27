using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SkyScope.Core;

public class FormNameDatabase
{
    private static readonly HashSet<string> SpelPerkGroups = new() { "SPEL", "PERK" };

    // (plugin.ToLower(), localFormId) -> (editorId, name)
    private readonly Dictionary<(string, uint), (string? EditorId, string? Name)> _lookup = new();

    // editorId (case-insensitive) -> full name
    private readonly Dictionary<string, string> _editorIdToName = new(StringComparer.OrdinalIgnoreCase);

    // all plugin filenames present in the current load order (case-insensitive)
    private readonly HashSet<string> _loadedPlugins = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded    { get; private set; }
    public int  RecordCount => _lookup.Count;

    public void Load(string skyrimGameDirectory, IProgress<string>? progress = null)
    {
        _lookup.Clear();
        _editorIdToName.Clear();
        _loadedPlugins.Clear();
        IsLoaded = false;

        var dataDir     = Path.Combine(skyrimGameDirectory, "Data");
        var pluginPaths = GetOrderedPluginPaths(skyrimGameDirectory, dataDir);

        progress?.Report($"Scanning {pluginPaths.Count} plugin(s) for Spell/Perk records…");

        var parser = new EsmFormParser();

        foreach (var pluginPath in pluginPaths)
        {
            var fileName = Path.GetFileName(pluginPath);
            _loadedPlugins.Add(fileName);
            try
            {
                var parsed = parser.Parse(pluginPath, SpelPerkGroups);

                foreach (var entry in parsed.Entries)
                {
                    var key = (entry.OriginalPlugin.ToLowerInvariant(), entry.LocalFormId);

                    if (_lookup.TryGetValue(key, out var existing))
                    {
                        _lookup[key] = (
                            string.IsNullOrEmpty(entry.EditorId) ? existing.EditorId : entry.EditorId,
                            string.IsNullOrEmpty(entry.FullName) ? existing.Name     : entry.FullName
                        );
                    }
                    else
                    {
                        _lookup[key] = (entry.EditorId, entry.FullName);
                    }

                    if (!string.IsNullOrEmpty(entry.EditorId) && !string.IsNullOrEmpty(entry.FullName))
                        _editorIdToName[entry.EditorId] = entry.FullName;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"  [warn] Skipped {fileName}: {ex.Message}");
            }
        }

        IsLoaded = true;
        progress?.Report($"Form database ready — {_lookup.Count:N0} spell/perk record(s) indexed.");
    }

    // Resolves a full RuleValue string which may contain comma-separated form references.
    // Each token can be:
    //   SPID format:       0x2CD7E~Apocalypse - Magic of Skyrim.esp
    //   SkyPatcher format: Apocalypse - Magic of Skyrim.esp|0x2CD7E  or  Plugin.esp|12345
    //   EditorId:          MagicFireBolt
    // Returns enriched display string, or the original value if resolution fails.
    public string ResolveRuleValue(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) return ruleValue;

        var parts    = ruleValue.Split(',');
        var resolved = parts.Select(p =>
        {
            var display = ResolveToken(p.Trim());
            return display ?? p.Trim();
        });
        return string.Join(", ", resolved);
    }

    // Returns true if at least one form reference token in a comma-separated rule value
    // names a plugin that is present in the current load order.
    // EditorId tokens (no ~ or | separator) are treated as potentially valid because we
    // cannot determine their source plugin without resolving them.
    public bool HasAnyLoadedPlugin(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) return false;

        foreach (var part in ruleValue.Split(','))
        {
            var token = part.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            var tildeIdx = token.IndexOf('~');
            if (tildeIdx > 0)
            {
                var plugin = token[(tildeIdx + 1)..].Trim();
                if (_loadedPlugins.Contains(plugin)) return true;
                continue;
            }

            var pipeIdx = token.IndexOf('|');
            if (pipeIdx > 0)
            {
                var plugin = token[..pipeIdx].Trim();
                if (_loadedPlugins.Contains(plugin)) return true;
                continue;
            }

            // EditorId or unrecognised format — assume it may be valid
            return true;
        }
        return false;
    }

    private string? ResolveToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        // SPID: 0x<hex>~<plugin>
        var tildeIdx = token.IndexOf('~');
        if (tildeIdx > 0)
        {
            var formPart   = token[..tildeIdx].Trim();
            var pluginPart = token[(tildeIdx + 1)..].Trim();
            return ResolveFormId(pluginPart, formPart);
        }

        // SkyPatcher: <plugin>|<formId>
        var pipeIdx = token.IndexOf('|');
        if (pipeIdx > 0)
        {
            var pluginPart = token[..pipeIdx].Trim();
            var formPart   = token[(pipeIdx + 1)..].Trim();
            return ResolveFormId(pluginPart, formPart);
        }

        // Bare hex — no plugin context, cannot resolve
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        if (uint.TryParse(token, out _)) return null;

        // Plain EditorId — try to enrich with full name
        return ResolveEditorId(token);
    }

    private string? ResolveFormId(string plugin, string formIdStr)
    {
        if (formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            formIdStr = formIdStr[2..];

        if (!uint.TryParse(formIdStr, NumberStyles.HexNumber, null, out var rawFormId))
            return null;

        uint localFormId = rawFormId & 0x00FFFFFF;
        var  key         = (plugin.ToLowerInvariant(), localFormId);

        if (!_lookup.TryGetValue(key, out var entry)) return null;
        return FormatDisplay(entry.EditorId, entry.Name);
    }

    private string? ResolveEditorId(string editorId)
    {
        if (_editorIdToName.TryGetValue(editorId, out var name))
            return FormatDisplay(editorId, name);
        // EditorId not in database — return as-is (already human-readable)
        return editorId;
    }

    private static string FormatDisplay(string? editorId, string? name)
    {
        if (!string.IsNullOrEmpty(name) &&
            !string.Equals(name, editorId, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({editorId})";
        return editorId ?? name ?? string.Empty;
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
                var trimmed = line.TrimStart('*').Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var fullPath = Path.Combine(dataDir, trimmed);
                if (File.Exists(fullPath)) ordered.Add(fullPath);
            }

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
        var gameSide = Path.Combine(skyrimGameDirectory, "plugins.txt");
        if (File.Exists(gameSide)) return gameSide;
        return null;
    }
}
