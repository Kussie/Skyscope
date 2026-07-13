using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

// Report tab rendering, analysis log, and text-report export.
public partial class MainWindow
{
    private void DisplayResults(ConflictSummary summary, BosConflictSummary bosSummary)
    {
        SkyPatcherFilesText.Text     = _stats.SkyPatcherFilesScanned.ToString("N0");
        SkyPatcherSupportedText.Text = _stats.SkyPatcherSupportedFiles.ToString("N0");
        SkyPatcherRulesText.Text     = _stats.SkyPatcherRuleCount.ToString("N0");
        SpidFilesText.Text           = _stats.SpidFileCount.ToString("N0");
        SpidSupportedText.Text       = _stats.SpidSupportedFiles.ToString("N0");
        SpidRulesText.Text           = _stats.SpidRuleCount.ToString("N0");
        BosFilesText.Text            = _stats.BosFileCount.ToString("N0");
        BosSupportedText.Text        = _stats.BosSupportedFiles.ToString("N0");
        BosRulesText.Text            = _stats.BosRuleCount.ToString("N0");
        NpcDbRecordsText.Text     = $"{_stats.DbRecordCount:N0}";

        var appearance = ConflictResolutionHelper.FilterLowChanceSpid(summary.AppearanceConflicts, showLowChance: false);
        var skin       = ConflictResolutionHelper.FilterLowChanceSpid(summary.SkinConflicts,       showLowChance: false);
        var outfit     = ConflictResolutionHelper.FilterLowChanceSpid(summary.OutfitDefaultConflicts, showLowChance: false);

        var appearanceCount = CountDistinctNpcs(appearance);
        var skinCount       = CountDistinctNpcs(skin);
        var outfitCount     = CountDistinctNpcs(outfit);
        var spellCount      = CountDistinctNpcs(summary.SpellConflicts);
        var perkCount       = CountDistinctNpcs(summary.PerkConflicts);

        AppearanceSummaryText.Text = SummaryLine(appearanceCount);
        SkinSummaryText.Text       = SummaryLine(skinCount);
        OutfitSummaryText.Text     = SummaryLine(outfitCount);
        SpellSummaryText.Text      = SummaryLine(spellCount);
        PerkSummaryText.Text       = SummaryLine(perkCount);

        var totalAll    = appearanceCount + skinCount + outfitCount + spellCount + perkCount + bosSummary.TotalConflicts;
        var activeTypes = (appearanceCount > 0 ? 1 : 0)
                        + (skinCount       > 0 ? 1 : 0)
                        + (outfitCount     > 0 ? 1 : 0)
                        + (spellCount      > 0 ? 1 : 0)
                        + (perkCount       > 0 ? 1 : 0)
                        + (bosSummary.TotalConflicts > 0 ? 1 : 0);
        TotalSummaryText.Text = totalAll == 0
            ? "No conflicts detected"
            : $"{totalAll} conflict(s) across {activeTypes} type(s)";

        ReportPlaceholderText.Visibility = Visibility.Collapsed;
        ReportSummaryPanel.Visibility    = Visibility.Visible;
    }

    private static string SummaryLine(int count) =>
        count == 0 ? "No conflicts" : $"{count} NPC(s) affected";

    private static int CountDistinctNpcs(List<ConflictEntry> entries) =>
        entries
            .Select(e => !string.IsNullOrEmpty(e.ResolvedEditorId) ? e.ResolvedEditorId : e.NpcRef.NormalizedKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private void ClearResults()
    {
        ReportSummaryPanel.Visibility    = Visibility.Collapsed;
        ReportPlaceholderText.Visibility = Visibility.Visible;
        ExportReportButton.IsEnabled = false;
        NpcConflictViewControl.Clear();
        BosConflictViewControl.Clear();
        _lastSummary    = null;
        _lastBosSummary = null;
        _stats          = new ScanStats();
    }

    private static void WriteAnalysisLog(
        string                 skyrimPath,
        List<ModConfiguration> skyPatcherConfigs,
        int                    skyPatcherFilesScanned,
        List<string>           skyPatcherErrors,
        List<DistributionRule>   spidRules,
        int                    spidFilesScanned,
        List<string>           spidErrors,
        List<BosSwapRule>      bosRules,
        int                    bosFilesScanned,
        List<string>           bosErrors)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
            var sb      = new StringBuilder();

            sb.AppendLine("SkyScope Analysis Log");
            sb.AppendLine($"Timestamp : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // ── SkyPatcher ──────────────────────────────────────────────────
            var skyPatcherRoot = Path.Combine(skyrimPath, @"Data\SKSE\Plugins\SkyPatcher");
            sb.AppendLine("--- SkyPatcher Scan ---");
            sb.AppendLine($"Path   : {skyPatcherRoot}");
            sb.AppendLine($"Exists : {Directory.Exists(skyPatcherRoot)}");
            sb.AppendLine($"Files  : {skyPatcherFilesScanned} scanned, {skyPatcherConfigs.Count} with rules");
            if (skyPatcherErrors.Count > 0)
            {
                sb.AppendLine($"Errors : {skyPatcherErrors.Count}");
                foreach (var err in skyPatcherErrors) sb.AppendLine($"  {err}");
            }
            else
            {
                sb.AppendLine("Errors : 0");
            }
            sb.AppendLine();

            var skyPatcherFiles = skyPatcherConfigs.Select(c => c.FilePath).OrderBy(f => f).ToList();
            sb.AppendLine($"--- SkyPatcher Files ({skyPatcherFiles.Count}) ---");
            foreach (var f in skyPatcherFiles) sb.AppendLine(f);
            sb.AppendLine();

            // ── SPID ────────────────────────────────────────────────────────
            var dataRoot = Path.Combine(skyrimPath, "Data");
            sb.AppendLine("--- SPID Scan ---");
            sb.AppendLine($"Path   : {dataRoot}");
            sb.AppendLine($"Exists : {Directory.Exists(dataRoot)}");
            sb.AppendLine($"Files  : {spidFilesScanned} scanned");
            if (spidErrors.Count > 0)
            {
                sb.AppendLine($"Errors : {spidErrors.Count}");
                foreach (var err in spidErrors) sb.AppendLine($"  {err}");
            }
            else
            {
                sb.AppendLine("Errors : 0");
            }
            sb.AppendLine();

            var spidFiles = spidRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
            sb.AppendLine($"--- SPID Files ({spidFiles.Count}) ---");
            foreach (var f in spidFiles) sb.AppendLine(f);
            sb.AppendLine();

            // ── BOS ─────────────────────────────────────────────────────────
            sb.AppendLine("--- BOS Scan ---");
            sb.AppendLine($"Path   : {dataRoot}");
            sb.AppendLine($"Exists : {Directory.Exists(dataRoot)}");
            sb.AppendLine($"Files  : {bosFilesScanned} scanned");
            if (bosErrors.Count > 0)
            {
                sb.AppendLine($"Errors : {bosErrors.Count}");
                foreach (var err in bosErrors) sb.AppendLine($"  {err}");
            }
            else
            {
                sb.AppendLine("Errors : 0");
            }
            sb.AppendLine();

            var bosFiles = bosRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
            sb.AppendLine($"--- BOS Files ({bosFiles.Count}) ---");
            foreach (var f in bosFiles) sb.AppendLine(f);

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    private void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSummary is null) return;
        try
        {
            var report    = BuildTextReport(_lastSummary, _lastBosSummary);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var filename  = $"SkyScope_Report_{timestamp}.txt";
            var filepath  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            File.WriteAllText(filepath, report);
            StatusTextBlock.Text = $"Report exported to {filename}";
            System.Windows.MessageBox.Show($"Report saved to:\n{filepath}", "Export Successful",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to export report: {ex.Message}";
        }
    }

    private string BuildTextReport(ConflictSummary summary, BosConflictSummary? bosSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SkyScope Conflict Report ===");
        sb.AppendLine($"Generated:              {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"SkyPatcher files:       {summary.TotalFilesScanned}");
        sb.AppendLine($"SPID files:             {_stats.SpidFileCount}");
        sb.AppendLine($"BOS files:              {_stats.BosFileCount}");
        sb.AppendLine($"NPC database records:   {_stats.DbRecordCount:N0}");
        sb.AppendLine($"Total conflicts:        {summary.TotalConflicts + (bosSummary?.TotalConflicts ?? 0)}");
        sb.AppendLine();
        AppendSection(sb, "Appearance Conflicts (copyVisualStyle)",           summary.AppearanceConflicts);
        AppendSection(sb, "Skin Conflicts (skin)",                             summary.SkinConflicts);
        AppendSection(sb, "Default Outfit Conflicts (outfitDefault)",          summary.OutfitDefaultConflicts);
        AppendSection(sb, "Spell Conflicts (spellsToAdd / SPID Spell=)",       summary.SpellConflicts);
        AppendSection(sb, "Perk Conflicts (perksToAdd / SPID Perk=)",          summary.PerkConflicts);
        if (bosSummary != null) AppendBosSection(sb, bosSummary);
        return sb.ToString();
    }

    private static void AppendBosSection(StringBuilder sb, BosConflictSummary bosSummary)
    {
        sb.AppendLine($"--- Base Object Swap Conflicts (*_SWAP.ini) ({bosSummary.TotalConflicts}) ---");
        if (bosSummary.TotalConflicts == 0)
        {
            sb.AppendLine("  None");
        }
        else
        {
            foreach (var entry in bosSummary.SwapConflicts)
            {
                sb.AppendLine($"  {entry.DisplayName}");
                var winner = entry.Sources.Count > 0
                    ? Path.GetFileName(entry.Sources[^1].FilePath)
                    : "?";
                sb.AppendLine($"    Winner (alphabetical): {winner}");
                foreach (var src in entry.Sources)
                    sb.AppendLine($"    - {src.FilePath}  →  {src.SwapTarget}");
            }
        }
        sb.AppendLine();
    }

    private static void AppendSection(StringBuilder sb, string title, List<ConflictEntry> entries)
    {
        sb.AppendLine($"--- {title} ({entries.Count}) ---");
        if (entries.Count == 0)
        {
            sb.AppendLine("  None");
        }
        else
        {
            foreach (var entry in entries)
            {
                var npc     = entry.NpcRef;

                // Config sources (SPID/SkyPatcher) rank above plugin overhaul sources, which sort
                // by load order — matching the conflict panel. A plugin can only win when there is
                // no config source (plugin-vs-plugin).
                var configSorted = entry.Sources
                    .Where(s => s.SourceTool != "Plugin")
                    .OrderBy(s => s.FilePath, SkyPatcherLoadOrderComparer.Instance)
                    .ToList();
                var pluginSorted = entry.Sources
                    .Where(s => s.SourceTool == "Plugin")
                    .OrderBy(s => s.LoadOrderIndex ?? int.MaxValue)
                    .ToList();
                var sorted    = configSorted.Concat(pluginSorted).ToList();
                var winnerSrc = configSorted.Count > 0 ? configSorted[^1]
                              : pluginSorted.Count > 0 ? pluginSorted[^1] : null;
                var winner    = winnerSrc != null ? Path.GetFileName(winnerSrc.FilePath) : "?";

                if (!string.IsNullOrEmpty(entry.ResolvedName))
                    sb.AppendLine($"  {entry.ResolvedName}  [{npc.DisplayText}]");
                else
                    sb.AppendLine($"  {npc.DisplayText}");

                if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
                    sb.AppendLine($"    EditorID: {entry.ResolvedEditorId}");

                sb.AppendLine($"    Winner (load order): {winner}");
                foreach (var src in sorted)
                    sb.AppendLine($"    - {src.FilePath}{(src.SourceTool == "Plugin" ? "  [Plugin]" : "")}");
            }
        }
        sb.AppendLine();
    }
}
