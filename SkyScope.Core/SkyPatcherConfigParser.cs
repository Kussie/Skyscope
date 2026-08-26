using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class SkyPatcherConfigParser
{
    private const string SkyPatcherBase = @"Data\SKSE\Plugins\SkyPatcher";

    // Returns the SkyPatcher root path. LoadConfigurationsFromDirectory scans recursively,
    // so all subdirectory layouts (npc/, outfit/, custom mod folders, root-level files) are covered.
    public string GetSkyPatcherRootPath(string skyrimDirectory)
    {
        if (!Directory.Exists(skyrimDirectory))
            throw new DirectoryNotFoundException($"Skyrim directory not found: {skyrimDirectory}");

        var basePath = Path.Combine(skyrimDirectory, SkyPatcherBase);
        if (!Directory.Exists(basePath))
            throw new DirectoryNotFoundException($"SkyPatcher directory not found at: {basePath}");

        return basePath;
    }

    public (List<ModConfiguration> Configs, int FilesScanned, List<string> Errors) LoadConfigurationsFromSkyrimDirectory(
        string skyrimDirectory, EditOutputOptions outputOptions = default)
    {
        var rootPath = GetSkyPatcherRootPath(skyrimDirectory);
        return LoadConfigurationsFromDirectory(rootPath, outputOptions);
    }

    public (List<ModConfiguration> Configs, int FilesScanned, List<string> Errors) LoadConfigurationsFromDirectory(
        string directoryPath, EditOutputOptions outputOptions = default)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var configs = new List<ModConfiguration>();
        var errors  = new List<string>();

        // Sorted by full path so configs are processed in SkyPatcher's load order.
        var iniFiles = ConfigFiles.Enumerate(directoryPath, "*.ini");

        foreach (var filePath in iniFiles)
        {
            try
            {
                var config = ParseConfigFile(filePath, outputOptions);
                if (config.Rules.Count > 0)
                    configs.Add(config);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse {filePath}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to parse {filePath}: {ex.Message}");
            }
        }

        return (configs, iniFiles.Length, errors);
    }

    // Format (no section headers) — multiple rule types may appear on one line:
    //   filterByNpcs=Plugin|FormId[,Plugin|FormId]:copyVisualStyle=V:skin=V
    //   filterByNpcEditorIds=EditorId:skin=Plugin|FormId
    //   filterByNpcNames=Name:outfitDefault=Plugin|FormId
    public ModConfiguration ParseConfigFile(string filePath, EditOutputOptions outputOptions = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Configuration file not found: {filePath}");

        var config = new ModConfiguration
        {
            FilePath = filePath,
            ModName  = Path.GetFileNameWithoutExtension(filePath)
        };

        // Read from the redirected output copy if one exists — some mod managers' virtual
        // filesystems don't reliably reflect it, so we check for it explicitly.
        var readPath = EditOutputPathResolver.ResolveForRead(filePath, outputOptions);
        var lines = File.ReadAllLines(readPath);

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
    private IEnumerable<DistributionRule> ParseRuleLine(
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
                case "spellstoadd":
                    rulePairs.Add((RuleType.Spell, val));
                    break;
                case "perkstoadd":
                    rulePairs.Add((RuleType.Perk, val));
                    break;
            }
        }

        if (filterValue == null || rulePairs.Count == 0) yield break;

        var targetNpcs = ParseNpcList(filterValue, refType);
        if (targetNpcs.Count == 0) yield break;

        foreach (var (ruleType, ruleValue) in rulePairs)
        {
            yield return new DistributionRule
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
