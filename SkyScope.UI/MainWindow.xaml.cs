using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using Microsoft.Win32;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;


public partial class MainWindow : Window
{
    private ConflictSummary?    _lastSummary;
    private BosConflictSummary? _lastBosSummary;
    private int _lastSpidFileCount;
    private int _lastBosFileCount;
    private int _lastDbRecordCount;
    private int _lastSkyPatcherRuleCount;
    private int _lastSpidRuleCount;
    private int _lastBosRuleCount;
    private const string SettingsFileName = "skyscope_settings.txt";

    private readonly HistoryStore _historyStore = new();

    public MainWindow()
    {
        InitializeComponent();
        _historyStore.Load();
        NpcConflictViewControl.HistoryStore = _historyStore;
        BosConflictViewControl.HistoryStore = _historyStore;
        HistoryViewControl.Refresh(_historyStore);
        LoadSettings();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description            = "Select your Skyrim game directory",
            UseDescriptionForTitle = true,
            ShowNewFolderButton    = false
        };

        var current = SkyrimPathTextBox.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SkyrimPathTextBox.Text = dialog.SelectedPath;
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var skyrimPath = SkyrimPathTextBox.Text?.Trim();

        if (string.IsNullOrEmpty(skyrimPath))
        {
            StatusTextBlock.Text = "Please specify a Skyrim game directory.";
            return;
        }

        if (!Directory.Exists(skyrimPath))
        {
            StatusTextBlock.Text = "Skyrim directory does not exist.";
            return;
        }

        AnalyzeButton.IsEnabled = false;

        try
        {
            // ── Step 1: Parse all config files (no plugin I/O) ─────────────

            StatusTextBlock.Text = "Scanning SkyPatcher configs…";

            List<ModConfiguration> configs;
            try
            {
                configs = await Task.Run(() =>
                    new SkyPatcherConfigParser().LoadConfigurationsFromSkyrimDirectory(skyrimPath));
            }
            catch (DirectoryNotFoundException ex)
            {
                StatusTextBlock.Text = ex.Message;
                return;
            }

            if (configs.Count == 0)
            {
                StatusTextBlock.Text = $"No SkyPatcher rule files found under {skyrimPath}. Check that SkyPatcher is installed and the game path is correct.";
                ClearResults();
                return;
            }

            StatusTextBlock.Text = "Scanning SPID distribution files…";
            var (spidRules, spidFileCount) = await Task.Run(() =>
                new SpidConfigParser().LoadDistributionRulesFromDirectory(Path.Combine(skyrimPath, "Data")));

            StatusTextBlock.Text = "Scanning Base Object Swapper files…";
            var (bosRules, bosFileCount) = await Task.Run(() =>
                new BosConfigParser().LoadSwapRulesFromDirectory(Path.Combine(skyrimPath, "Data")));

            // ── Step 2: Build reference library from parsed rules ───────────

            StatusTextBlock.Text = "Building reference library…";
            var loadedPlugins = await Task.Run(() => PluginPathResolver.GetOrderedPluginNames(skyrimPath));
            var library       = new ModReferenceLibrary();
            library.SetLoadedPlugins(loadedPlugins);
            await Task.Run(() => new ReferenceExtractor().Extract(library, configs, spidRules, bosRules));

            // ── Step 3: Enrich library from plugin files ────────────────────

            var progress = new Progress<string>(msg => StatusTextBlock.Text = msg);
            await Task.Run(() => new PluginEnricher().Enrich(library, skyrimPath, progress));
            _lastDbRecordCount = library.NpcRecordCount;

            // ── Step 4: Bundle SPID rules + filter inactive spell/perk ──────

            var spidSpellPerkConfigs = spidRules
                .Where(r => r.RuleType is RuleType.Spell or RuleType.Perk)
                .GroupBy(r => r.SourceFile, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ModConfiguration
                {
                    FilePath = g.Key,
                    ModName  = Path.GetFileName(g.Key),
                    Rules    = g.ToList()
                })
                .ToList();

            var allConfigs = configs.Concat(spidSpellPerkConfigs).ToList();

            foreach (var config in allConfigs)
                config.Rules.RemoveAll(r =>
                    (r.RuleType is RuleType.Spell or RuleType.Perk) &&
                    !library.HasAnyLoadedPlugin(r.RuleValue));
            allConfigs.RemoveAll(c => c.Rules.Count == 0);

            // ── Step 5: Detect conflicts ────────────────────────────────────

            StatusTextBlock.Text = "Detecting conflicts…";
            var summary = await Task.Run(() =>
            {
                var s = new ConflictDetector().DetectConflicts(allConfigs, library);
                s.TotalFilesScanned = configs.Count;
                return s;
            });

            await Task.Run(() => ResolveNames(summary, library));

            var spidOutfitRules = spidRules.Where(r => r.RuleType == RuleType.OutfitDefault).ToList();
            if (spidOutfitRules.Count > 0)
            {
                StatusTextBlock.Text = $"Found {spidOutfitRules.Count} SPID outfit rule(s). Merging conflicts…";
                await Task.Run(() => MergeSpidConflicts(summary, configs, spidOutfitRules, library));
            }

            var bosSummary = await Task.Run(() =>
                new BosConflictDetector().DetectConflicts(bosRules, library));
            bosSummary.FilesScanned = bosFileCount;

            _lastSpidFileCount       = spidFileCount;
            _lastBosFileCount        = bosFileCount;
            _lastSkyPatcherRuleCount = configs.Sum(c => c.Rules.Count);
            _lastSpidRuleCount       = spidRules.Count;
            _lastBosRuleCount        = bosRules.Count;
            _lastSummary             = summary;
            _lastBosSummary          = bosSummary;
            DisplayResults(summary, bosSummary);

            if (App.VerboseMode)
                WriteAnalysisLog(configs, spidRules, bosRules);
            NpcConflictViewControl.Populate(summary, library);
            BosConflictViewControl.Populate(bosSummary);
            ExportReportButton.IsEnabled = summary.TotalConflicts > 0 || bosSummary.TotalConflicts > 0;

            var spidSpellPerkCount = spidRules.Count(r => r.RuleType is RuleType.Spell or RuleType.Perk);
            var spidSuffix = spidFileCount > 0
                ? $"  SPID: {spidFileCount} file(s), {spidOutfitRules.Count} outfit rule(s), {spidSpellPerkCount} spell/perk rule(s)."
                : string.Empty;
            var bosSuffix = bosFileCount > 0
                ? $"  BOS: {bosFileCount} file(s), {bosSummary.TotalConflicts} conflict(s)."
                : string.Empty;
            var totalConflicts = summary.TotalConflicts + bosSummary.TotalConflicts;
            StatusTextBlock.Text = totalConflicts == 0
                ? $"Analysis complete — no conflicts in {configs.Count} SkyPatcher file(s).  NPC database: {library.NpcRecordCount:N0} record(s).{spidSuffix}{bosSuffix}"
                : $"Analysis complete — {totalConflicts} conflict(s) in {configs.Count} SkyPatcher file(s).  NPC database: {library.NpcRecordCount:N0} record(s).{spidSuffix}{bosSuffix}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
            System.Windows.MessageBox.Show($"An error occurred:\n\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
        }
    }

    private static void ResolveNames(ConflictSummary summary, ModReferenceLibrary library)
    {
        foreach (var entry in AllEntries(summary))
        {
            if (entry.NpcRef.RefType == NpcRefType.RecordId)
            {
                entry.ResolvedName     = library.ResolveName(entry.NpcRef.Plugin, entry.NpcRef.FormId);
                entry.ResolvedEditorId = library.ResolveEditorId(entry.NpcRef.Plugin, entry.NpcRef.FormId);
            }
            else if (entry.NpcRef.RefType == NpcRefType.LocalFormId)
            {
                var (eid, name)        = library.ResolveByLocalFormId(entry.NpcRef.Identifier);
                entry.ResolvedEditorId = eid;
                entry.ResolvedName     = name;
            }
        }
    }

    private static void MergeSpidConflicts(
        ConflictSummary summary,
        List<ModConfiguration> skyPatcherConfigs,
        List<SkyPatcherRule> spidRules,
        ModReferenceLibrary library)
    {
        // Build EditorId index of every SkyPatcher outfit rule (not just conflicting ones).
        var spByEditorId = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in skyPatcherConfigs)
        {
            foreach (var rule in config.Rules)
            {
                if (rule.RuleType != RuleType.OutfitDefault) continue;

                foreach (var npcRef in rule.TargetNpcs)
                {
                    var eid = npcRef.RefType switch
                    {
                        NpcRefType.RecordId => library.ResolveEditorId(npcRef.Plugin, npcRef.FormId),
                        NpcRefType.EditorId => npcRef.Identifier,
                        NpcRefType.Name     => library.FindEditorIdByName(npcRef.Identifier),
                        _                   => null
                    };

                    if (string.IsNullOrEmpty(eid)) continue;

                    if (!spByEditorId.TryGetValue(eid, out var list))
                        spByEditorId[eid] = list = new();

                    if (!list.Exists(r => string.Equals(r.SourceFile, rule.SourceFile, StringComparison.OrdinalIgnoreCase)))
                        list.Add(rule);
                }
            }
        }

        // Index existing conflict entries by resolved EditorId.
        var conflictByEditorId = new Dictionary<string, ConflictEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in summary.OutfitDefaultConflicts)
        {
            if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
                conflictByEditorId[entry.ResolvedEditorId] = entry;
        }

        // Group SPID rules by resolved EditorId, recording which identifier text matched.
        // StringFilter refs (Name type) go through the name→EditorId reverse lookup first.
        var spidByEditorId = new Dictionary<string, (NpcReference NpcRef, List<(SkyPatcherRule Rule, string Identifier)> Entries)>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in spidRules)
        {
            foreach (var npcRef in rule.TargetNpcs)
            {
                string? eid;
                if (npcRef.RefType == NpcRefType.EditorId)
                {
                    // Reject values that aren't actually NPC EditorIds (factions, keywords, etc.)
                    if (!library.IsNpcEditorId(npcRef.Identifier)) continue;
                    eid = npcRef.Identifier;
                }
                else
                {
                    // SPID StringFilters match against both display names and EditorIds.
                    // Try exact name first; if that fails, check whether the string is itself
                    // a valid NPC EditorId (e.g. "Yngvar" for "Yngvar the Singer").
                    eid = library.FindEditorIdByName(npcRef.Identifier);
                    if (eid == null)
                    {
                        if (library.IsNpcEditorId(npcRef.Identifier))
                            eid = npcRef.Identifier;
                        else
                            continue;
                    }
                }

                if (!spidByEditorId.TryGetValue(eid, out var entry))
                    spidByEditorId[eid] = (npcRef, new());

                var entries = spidByEditorId[eid].Entries;
                if (!entries.Exists(e => string.Equals(e.Rule.SourceFile, rule.SourceFile, StringComparison.OrdinalIgnoreCase)))
                    entries.Add((rule, npcRef.Identifier));
            }
        }

        // Merge.
        foreach (var (eid, (npcRef, spidEntries)) in spidByEditorId)
        {
            spByEditorId.TryGetValue(eid, out var spRules);

            if (conflictByEditorId.TryGetValue(eid, out var existing))
            {
                foreach (var (r, identifier) in spidEntries)
                    existing.Sources.Add(ToConflictSource(r, identifier));
            }
            else
            {
                var totalSources = (spRules?.Count ?? 0) + spidEntries.Count;
                if (totalSources < 2) continue;

                var newEntry = new ConflictEntry
                {
                    NpcRef           = npcRef,
                    ResolvedEditorId = eid,
                };

                if (spRules != null)
                    foreach (var r in spRules)
                        newEntry.Sources.Add(ToConflictSource(r));

                foreach (var (r, identifier) in spidEntries)
                    newEntry.Sources.Add(ToConflictSource(r, identifier));

                summary.OutfitDefaultConflicts.Add(newEntry);
            }
        }
    }

    private static ConflictSource ToConflictSource(SkyPatcherRule rule, string? spidNpcIdentifier = null) => new()
    {
        FilePath           = rule.SourceFile,
        LineNumber         = rule.LineNumber,
        PrecedingLine      = rule.PrecedingLine,
        ConflictLine       = rule.LineText,
        FollowingLine      = rule.FollowingLine,
        RuleValue          = rule.RuleValue,
        SourceTool         = rule.SourceTool,
        SpidChance         = rule.SpidChance,
        SpidNpcIdentifier  = spidNpcIdentifier
    };

    private static IEnumerable<ConflictEntry> AllEntries(ConflictSummary s) =>
        s.AppearanceConflicts.Concat(s.SkinConflicts).Concat(s.OutfitDefaultConflicts)
         .Concat(s.SpellConflicts).Concat(s.PerkConflicts);

    private void DisplayResults(ConflictSummary summary, BosConflictSummary bosSummary)
    {
        SkyPatcherFilesText.Text  = summary.TotalFilesScanned.ToString("N0");
        SkyPatcherRulesText.Text  = _lastSkyPatcherRuleCount.ToString("N0");
        SpidFilesText.Text        = _lastSpidFileCount.ToString("N0");
        SpidRulesText.Text        = _lastSpidRuleCount.ToString("N0");
        BosFilesText.Text         = _lastBosFileCount.ToString("N0");
        BosRulesText.Text         = _lastBosRuleCount.ToString("N0");
        NpcDbRecordsText.Text     = $"{_lastDbRecordCount:N0}";

        AppearanceSummaryText.Text = SummaryLine(summary.AppearanceConflicts.Count);
        SkinSummaryText.Text       = SummaryLine(summary.SkinConflicts.Count);
        OutfitSummaryText.Text     = SummaryLine(summary.OutfitDefaultConflicts.Count);
        SpellSummaryText.Text      = SummaryLine(summary.SpellConflicts.Count);
        PerkSummaryText.Text       = SummaryLine(summary.PerkConflicts.Count);
        BosSummaryText.Text        = BosSummaryLine(bosSummary.TotalConflicts);

        var totalAll    = summary.TotalConflicts + bosSummary.TotalConflicts;
        var activeTypes = (summary.AppearanceConflicts.Count    > 0 ? 1 : 0)
                        + (summary.SkinConflicts.Count          > 0 ? 1 : 0)
                        + (summary.OutfitDefaultConflicts.Count > 0 ? 1 : 0)
                        + (summary.SpellConflicts.Count         > 0 ? 1 : 0)
                        + (summary.PerkConflicts.Count          > 0 ? 1 : 0)
                        + (bosSummary.TotalConflicts            > 0 ? 1 : 0);
        TotalSummaryText.Text = totalAll == 0
            ? "No conflicts detected"
            : $"{totalAll} conflict(s) across {activeTypes} type(s)";

        ReportPlaceholderText.Visibility = Visibility.Collapsed;
        ReportSummaryPanel.Visibility    = Visibility.Visible;
    }

    private static string SummaryLine(int count) =>
        count == 0 ? "No conflicts" : $"{count} NPC(s) affected";

    private static string BosSummaryLine(int count) =>
        count == 0 ? "No conflicts" : $"{count} object(s) affected";

    private void ClearResults()
    {
        ReportSummaryPanel.Visibility    = Visibility.Collapsed;
        ReportPlaceholderText.Visibility = Visibility.Visible;
        ExportReportButton.IsEnabled = false;
        NpcConflictViewControl.Clear();
        BosConflictViewControl.Clear();
        _lastSummary             = null;
        _lastBosSummary          = null;
        _lastSpidFileCount       = 0;
        _lastBosFileCount        = 0;
        _lastDbRecordCount       = 0;
        _lastSkyPatcherRuleCount = 0;
        _lastSpidRuleCount       = 0;
        _lastBosRuleCount        = 0;
    }

    private static void WriteAnalysisLog(
        List<ModConfiguration> skyPatcherConfigs,
        List<SkyPatcherRule>   spidRules,
        List<BosSwapRule>      bosRules)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
            var sb      = new StringBuilder();

            sb.AppendLine("SkyScope Analysis Log");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var skyPatcherFiles = skyPatcherConfigs.Select(c => c.FilePath).OrderBy(f => f).ToList();
            sb.AppendLine($"--- SkyPatcher Files ({skyPatcherFiles.Count}) ---");
            foreach (var f in skyPatcherFiles) sb.AppendLine(f);
            sb.AppendLine();

            var spidFiles = spidRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
            sb.AppendLine($"--- SPID Files ({spidFiles.Count}) ---");
            foreach (var f in spidFiles) sb.AppendLine(f);
            sb.AppendLine();

            var bosFiles = bosRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
            sb.AppendLine($"--- BOS Files ({bosFiles.Count}) ---");
            foreach (var f in bosFiles) sb.AppendLine(f);

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
            File.WriteAllLines(settingsPath, new[]
            {
                $"SkyrimPath:{SkyrimPathTextBox.Text}",
                $"HistoryDiffMode:{HistoryViewControl.DiffMode}"
            });
            StatusTextBlock.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to save settings: {ex.Message}";
        }
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
        sb.AppendLine($"SPID files:             {_lastSpidFileCount}");
        sb.AppendLine($"BOS files:              {_lastBosFileCount}");
        sb.AppendLine($"NPC database records:   {_lastDbRecordCount:N0}");
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
                    ? Path.GetFileName(entry.Sources[entry.Sources.Count - 1].FilePath)
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
                var sorted  = entry.Sources
                    .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var winner  = sorted.Count > 0 ? Path.GetFileName(sorted[sorted.Count - 1].FilePath) : "?";

                if (!string.IsNullOrEmpty(entry.ResolvedName))
                    sb.AppendLine($"  {entry.ResolvedName}  [{npc.DisplayText}]");
                else
                    sb.AppendLine($"  {npc.DisplayText}");

                if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
                    sb.AppendLine($"    EditorID: {entry.ResolvedEditorId}");

                sb.AppendLine($"    Winner (load order): {winner}");
                foreach (var src in sorted)
                    sb.AppendLine($"    - {src.FilePath}");
            }
        }
        sb.AppendLine();
    }

    private void LoadSettings()
    {
        // Auto-detect from registry first, then let saved settings override
        var detected = TryDetectSkyrimPath();
        if (!string.IsNullOrEmpty(detected))
            SkyrimPathTextBox.Text = detected;

        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
            if (!File.Exists(settingsPath)) return;
            foreach (var line in File.ReadAllLines(settingsPath))
            {
                if (line.StartsWith("SkyrimPath:"))
                {
                    var saved = line["SkyrimPath:".Length..];
                    if (!string.IsNullOrEmpty(saved))
                        SkyrimPathTextBox.Text = saved;
                }
                else if (line.StartsWith("HistoryDiffMode:"))
                {
                    var mode = line["HistoryDiffMode:".Length..];
                    if (Enum.TryParse<DiffMode>(mode, out var diffMode))
                        HistoryViewControl.DiffMode = diffMode;
                }
            }
        }
        catch { }
    }

    private static string? TryDetectSkyrimPath()
    {
        try
        {
            // When launched through MO2 (including Stock Game setups), MO2 sets the working
            // directory to the managed game folder. Check that first so Stock Game users don't
            // need to configure the path manually.
            var cwd = Directory.GetCurrentDirectory();
            if (IsSkyrimDirectory(cwd))
                return cwd;

            // (subkey under HKLM, value name)
            var candidates = new[]
            {
                // Steam App 489830 = Skyrim Special Edition / Anniversary Edition
                (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 489830", "InstallLocation"),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 489830",             "InstallLocation"),
                // Bethesda launcher install key
                (@"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "Installed Path"),
                (@"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",             "Installed Path"),
                // Steam App 72850 = Skyrim Legendary Edition (LE) fallback
                (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 72850", "InstallLocation"),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 72850",             "InstallLocation"),
            };

            foreach (var (subKey, valueName) in candidates)
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                           .OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string path && Directory.Exists(path))
                    return path;
            }
        }
        catch { }

        return null;
    }

    private static bool IsSkyrimDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "SkyrimSE.exe"))
            || File.Exists(Path.Combine(path, "TESV.exe"))
            || File.Exists(Path.Combine(path, "SkyrimVR.exe"));
    }
}
