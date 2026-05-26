using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class SkyPatcherConfigParser
{
    private const string SkyPatcherBase = @"Data\SKSE\Plugins\SkyPatcher";
    private const string NpcSubdir     = "npc";
    private const string OutfitSubdir  = "outfit";

    public List<string> FindSkyPatcherDirectories(string skyrimDirectory)
    {
        if (!Directory.Exists(skyrimDirectory))
            throw new DirectoryNotFoundException($"Skyrim directory not found: {skyrimDirectory}");

        var basePath = Path.Combine(skyrimDirectory, SkyPatcherBase);
        if (!Directory.Exists(basePath))
            throw new DirectoryNotFoundException($"SkyPatcher directory not found at: {basePath}");

        var result = new List<string>();
        var npcPath    = Path.Combine(basePath, NpcSubdir);
        var outfitPath = Path.Combine(basePath, OutfitSubdir);

        if (Directory.Exists(npcPath))    result.Add(npcPath);
        if (Directory.Exists(outfitPath)) result.Add(outfitPath);

        return result;
    }

    public List<ModConfiguration> LoadConfigurationsFromSkyrimDirectory(string skyrimDirectory)
    {
        var dirs = FindSkyPatcherDirectories(skyrimDirectory);
        var configs = new List<ModConfiguration>();

        foreach (var dir in dirs)
        {
            var dirConfigs = LoadConfigurationsFromDirectory(dir);
            configs.AddRange(dirConfigs);
        }

        return configs;
    }

    public List<ModConfiguration> LoadConfigurationsFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var configs = new List<ModConfiguration>();

        // Sort by full path so configs are processed in SkyPatcher's load order:
        // folders and files sorted alphanumerically, with the full path as the sort key.
        var iniFiles = Directory.GetFiles(directoryPath, "*.ini", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in iniFiles)
        {
            try
            {
                var config = ParseConfigFile(filePath);
                if (config.Rules.Count > 0)
                    configs.Add(config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to parse {filePath}: {ex.Message}");
            }
        }

        return configs;
    }

    // Format (no section headers) — multiple rule types may appear on one line:
    //   filterByNpcs=Plugin|FormId[,Plugin|FormId]:copyVisualStyle=V:skin=V
    //   filterByNpcEditorIds=EditorId:skin=Plugin|FormId
    //   filterByNpcNames=Name:outfitDefault=Plugin|FormId
    public ModConfiguration ParseConfigFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Configuration file not found: {filePath}");

        var config = new ModConfiguration
        {
            FilePath = filePath,
            ModName  = Path.GetFileNameWithoutExtension(filePath)
        };

        var lines = File.ReadAllLines(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            config.Rules.AddRange(ParseRuleLine(
                lines[i], filePath, i + 1,
                i > 0                ? lines[i - 1] : null,
                i < lines.Length - 1 ? lines[i + 1] : null));
        }

        return config;
    }

    // A single line can carry both a filter and multiple rule types, e.g.:
    //   filterByNpcs=Aela:copyVisualStyle=X:skin=Y
    // Split the whole line into key=value segments so every rule type is captured.
    private IEnumerable<SkyPatcherRule> ParseRuleLine(
        string line, string sourceFile, int lineNumber, string? preceding, string? following)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            yield break;

        // Collect all key=value segments separated by ':'
        string? filterValue = null;
        NpcRefType refType  = NpcRefType.EditorId;
        var rulePairs       = new List<(RuleType Type, string Value)>();

        foreach (var segment in trimmed.Split(':'))
        {
            var eqIdx = segment.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = segment[..eqIdx].Trim();
            var val = segment[(eqIdx + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "filterbynpcs":
                    filterValue = val;
                    refType     = NpcRefType.RecordId;
                    break;
                case "filterbynpceditorids":
                    filterValue = val;
                    refType     = NpcRefType.EditorId;
                    break;
                case "filterbynpcnames":
                    filterValue = val;
                    refType     = NpcRefType.Name;
                    break;
                case "copyvisualstyle":
                    rulePairs.Add((RuleType.Appearance,    val));
                    break;
                case "skin":
                    rulePairs.Add((RuleType.Skin,          val));
                    break;
                case "outfitdefault":
                    rulePairs.Add((RuleType.OutfitDefault, val));
                    break;
            }
        }

        if (filterValue == null || rulePairs.Count == 0) yield break;

        var targetNpcs = ParseNpcList(filterValue, refType);
        if (targetNpcs.Count == 0) yield break;

        foreach (var (ruleType, ruleValue) in rulePairs)
        {
            yield return new SkyPatcherRule
            {
                TargetNpcs    = targetNpcs,
                RuleType      = ruleType,
                RuleValue     = ruleValue,
                SourceFile    = sourceFile,
                LineNumber    = lineNumber,
                PrecedingLine = preceding,
                LineText      = line.Trim(),
                FollowingLine = following
            };
        }
    }

    private List<NpcReference> ParseNpcList(string value, NpcRefType refType)
    {
        var refs = new List<NpcReference>();

        foreach (var part in value.Split(','))
        {
            var npcRef = ParseNpcReference(part.Trim(), refType);
            if (npcRef != null) refs.Add(npcRef);
        }

        return refs;
    }

    private NpcReference? ParseNpcReference(string npcStr, NpcRefType refType)
    {
        if (string.IsNullOrEmpty(npcStr)) return null;

        if (refType == NpcRefType.RecordId)
        {
            var pipeIdx = npcStr.IndexOf('|');

            // filterByNpcs accepts either Plugin|FormId or a bare EditorId
            if (pipeIdx < 0)
                return new NpcReference { RefType = NpcRefType.EditorId, Identifier = npcStr };

            return new NpcReference
            {
                RefType = NpcRefType.RecordId,
                Plugin  = npcStr[..pipeIdx].Trim(),
                FormId  = npcStr[(pipeIdx + 1)..].Trim()
            };
        }

        return new NpcReference
        {
            RefType    = refType,
            Identifier = npcStr
        };
    }
}
