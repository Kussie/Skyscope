using System;
using System.Collections.Generic;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

// Flags issues with individual rules, independent of conflict detection (bad chance value,
// dangling reference, duplicated line). Deliberately skips SPID Field-1 string filters — those are
// ambiguous with keywords, and there's no keyword enrichment to rule that out safely.
public class ProblemDetector
{
    public ProblemSummary DetectProblems(
        List<ModConfiguration> skyPatcherConfigs,
        List<DistributionRule> spidRules,
        ModReferenceLibrary    library,
        List<string>           skyPatcherErrors,
        List<string>           spidErrors)
    {
        var summary = new ProblemSummary();

        foreach (var err in skyPatcherErrors)
            summary.SkyPatcherProblems.Add(ParseFileError(err, "SkyPatcher"));
        foreach (var err in spidErrors)
            summary.SpidProblems.Add(ParseFileError(err, "SPID"));

        var skyPatcherRules = skyPatcherConfigs.SelectMany(c => c.Rules).ToList();
        foreach (var rule in skyPatcherRules)
        {
            CheckUnresolvedNpcReferences(rule, library, summary.SkyPatcherProblems);
            CheckPluginNotLoaded(rule, library, summary.SkyPatcherProblems);
        }
        CheckDuplicateRules(skyPatcherRules, summary.SkyPatcherProblems);

        foreach (var rule in spidRules)
        {
            CheckSpidChance(rule, summary.SpidProblems);
            CheckPluginNotLoaded(rule, library, summary.SpidProblems);
        }
        CheckDuplicateRules(spidRules, summary.SpidProblems);

        return summary;
    }

    private static void CheckSpidChance(DistributionRule rule, List<ProblemEntry> sink)
    {
        if (rule.SpidChance is { } chance && (chance < 0 || chance > 100))
            sink.Add(NewEntry(rule, "Chance out of range",
                $"Chance value {chance} is outside the valid 0–100 range."));
    }

    private static void CheckUnresolvedNpcReferences(
        DistributionRule rule, ModReferenceLibrary library, List<ProblemEntry> sink)
    {
        foreach (var npcRef in rule.TargetNpcs)
        {
            var resolved = npcRef.RefType switch
            {
                NpcRefType.EditorId => library.IsNpcEditorId(npcRef.Identifier),
                NpcRefType.Name     => library.FindEditorIdByName(npcRef.Identifier) != null,
                // Skip the check when the plugin itself isn't loaded — CheckPluginNotLoaded already
                // flags that, more specifically, and we don't want to double up.
                NpcRefType.RecordId => !library.IsPluginLoaded(npcRef.Plugin)
                                       || library.ResolveEditorId(npcRef.Plugin, npcRef.FormId) != null,
                NpcRefType.LocalFormId => library.ResolveByLocalFormId(npcRef.Identifier).editorId != null,
                _ => true
            };

            if (!resolved)
                sink.Add(NewEntry(rule, "Unresolved NPC reference",
                    $"NPC reference '{npcRef.DisplayText}' was not found in any loaded plugin."));
        }
    }

    private static void CheckPluginNotLoaded(
        DistributionRule rule, ModReferenceLibrary library, List<ProblemEntry> sink)
    {
        foreach (var npcRef in rule.TargetNpcs)
            if (npcRef.RefType == NpcRefType.RecordId && !string.IsNullOrEmpty(npcRef.Plugin)
                && !library.IsPluginLoaded(npcRef.Plugin))
                sink.Add(NewEntry(rule, "Plugin not loaded",
                    $"References plugin '{npcRef.Plugin}', which isn't in the current load order."));

        foreach (var sf in rule.SpidStringFilters)
            if (!string.IsNullOrEmpty(sf.Plugin) && !library.IsPluginLoaded(sf.Plugin))
                sink.Add(NewEntry(rule, "Plugin not loaded",
                    $"References plugin '{sf.Plugin}', which isn't in the current load order."));

        foreach (var ff in rule.SpidFormFilters)
            if (!string.IsNullOrEmpty(ff.Plugin) && !library.IsPluginLoaded(ff.Plugin))
                sink.Add(NewEntry(rule, "Plugin not loaded",
                    $"References plugin '{ff.Plugin}', which isn't in the current load order."));

        foreach (var plugin in ExtractRuleValuePlugins(rule.RuleValue))
            if (!library.IsPluginLoaded(plugin))
                sink.Add(NewEntry(rule, "Plugin not loaded",
                    $"Distributes a record from plugin '{plugin}', which isn't in the current load order."));
    }

    // Same file + exact same line text on more than one distinct line number — combined-syntax
    // lines (same line, multiple rules) share a LineNumber so they're never flagged here.
    private static void CheckDuplicateRules(List<DistributionRule> rules, List<ProblemEntry> sink)
    {
        var groups = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.LineText))
            .GroupBy(r => (r.SourceFile, LineText: r.LineText.Trim()), StringGroupComparer.Instance);

        foreach (var group in groups)
        {
            var lineNumbers = group.Select(r => r.LineNumber).Distinct().ToList();
            if (lineNumbers.Count < 2) continue;

            foreach (var rule in group)
                sink.Add(NewEntry(rule, "Duplicate rule",
                    $"This exact line also appears at line(s) {string.Join(", ", lineNumbers.Where(n => n != rule.LineNumber))}."));
        }
    }

    private sealed class StringGroupComparer : IEqualityComparer<(string SourceFile, string LineText)>
    {
        public static readonly StringGroupComparer Instance = new();
        public bool Equals((string SourceFile, string LineText) x, (string SourceFile, string LineText) y) =>
            string.Equals(x.SourceFile, y.SourceFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.LineText, y.LineText, StringComparison.Ordinal);
        public int GetHashCode((string SourceFile, string LineText) obj) =>
            HashCode.Combine(obj.SourceFile.ToLowerInvariant(), obj.LineText);
    }

    private static IEnumerable<string> ExtractRuleValuePlugins(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) yield break;

        foreach (var part in ruleValue.Split(','))
        {
            var token = part.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            var tildeIdx = token.IndexOf('~');
            if (tildeIdx > 0) { yield return token[(tildeIdx + 1)..].Trim(); continue; }

            var pipeIdx = token.IndexOf('|');
            if (pipeIdx > 0) yield return token[..pipeIdx].Trim();
        }
    }

    private static ProblemEntry NewEntry(
        DistributionRule rule, string category, string message,
        ProblemSeverity severity = ProblemSeverity.Warning) => new()
    {
        FilePath   = rule.SourceFile,
        LineNumber = rule.LineNumber,
        LineText   = rule.LineText,
        SourceTool = rule.SourceTool,
        Severity   = severity,
        Category   = category,
        Message    = message
    };

    // Best-effort split of "Failed to parse {path}: {msg}" back into path/message; falls back to
    // the raw string if the format doesn't match.
    private static ProblemEntry ParseFileError(string error, string sourceTool)
    {
        const string prefix = "Failed to parse ";
        var filePath = error;
        var message  = error;

        if (error.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest   = error[prefix.Length..];
            var sepIdx = rest.IndexOf(": ", StringComparison.Ordinal);
            if (sepIdx > 0)
            {
                filePath = rest[..sepIdx];
                message  = rest[(sepIdx + 2)..];
            }
        }

        return new ProblemEntry
        {
            FilePath   = filePath,
            LineNumber = 0,
            LineText   = string.Empty,
            SourceTool = sourceTool,
            Severity   = ProblemSeverity.Error,
            Category   = "File failed to parse",
            Message    = message
        };
    }
}
