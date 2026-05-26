using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using System.Windows.Input;
using Microsoft.Win32;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public class ConflictDisplayItem
{
    public string        DisplayName    { get; init; } = string.Empty;
    public string        WinnerFileName { get; init; } = string.Empty;
    public string        FileCount      { get; init; } = string.Empty;
    public string        ToolTipText    { get; init; } = string.Empty;
    public ConflictEntry Entry          { get; init; } = new();
    public RuleType      RuleType       { get; init; }

    public static ConflictDisplayItem FromEntry(ConflictEntry entry, RuleType ruleType)
    {
        var npc = entry.NpcRef;

        // SkyPatcher loads files in alphanumeric order of their full path (folder structure
        // is part of the sort key, not just the filename). The last file loaded wins.
        var sorted     = entry.Sources
            .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var winnerPath = sorted.Count > 0 ? sorted[sorted.Count - 1].FilePath : string.Empty;
        var winnerFile = Path.GetFileNameWithoutExtension(winnerPath);

        var sb = new StringBuilder();

        // ── NPC identifier section ──────────────────────────────────────────
        sb.AppendLine("NPC Identifier");
        sb.AppendLine(new string('─', 36));

        if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
            sb.AppendLine($"  Editor ID:  {entry.ResolvedEditorId}");

        if (!string.IsNullOrEmpty(entry.ResolvedName) &&
            !string.Equals(entry.ResolvedName, entry.ResolvedEditorId, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"  Name:       {entry.ResolvedName}");

        if (npc.RefType == NpcRefType.RecordId)
        {
            sb.AppendLine($"  Plugin:     {npc.Plugin}");
            sb.AppendLine($"  Record ID:  {npc.FormId}");
        }
        else if (npc.RefType == NpcRefType.EditorId)
        {
            if (string.IsNullOrEmpty(entry.ResolvedEditorId))
                sb.AppendLine($"  Editor ID:  {npc.Identifier}");
        }
        else
        {
            if (string.IsNullOrEmpty(entry.ResolvedName))
                sb.AppendLine($"  Name:       {npc.Identifier}");
        }

        sb.AppendLine();

        // ── Conflicting files section ────────────────────────────────────────
        sb.AppendLine($"Conflicting Files ({entry.Sources.Count})  —  last by load path wins");
        sb.AppendLine(new string('─', 36));
        foreach (var src in sorted)
        {
            bool wins = string.Equals(src.FilePath, winnerPath, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"  {(wins ? "►" : " ")} {Path.GetFileName(src.FilePath)}");
            sb.AppendLine($"      {src.FilePath}");
        }

        return new ConflictDisplayItem
        {
            DisplayName    = entry.DisplayName,
            WinnerFileName = winnerFile,
            FileCount      = entry.Sources.Count.ToString(),
            ToolTipText    = sb.ToString().TrimEnd(),
            Entry          = entry,
            RuleType       = ruleType
        };
    }
}

public partial class MainWindow : Window
{
    private ConflictSummary? _lastSummary;
    private const string SettingsFileName = "skyscope_settings.txt";

    public MainWindow()
    {
        InitializeComponent();
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

            StatusTextBlock.Text = $"Found {configs.Count} INI file(s). Loading NPC database…";
            var progress = new Progress<string>(msg => StatusTextBlock.Text = msg);
            var db       = new NpcNameDatabase();
            await Task.Run(() => db.Load(skyrimPath, progress));

            StatusTextBlock.Text = "Detecting conflicts…";
            var summary = await Task.Run(() =>
            {
                var detector = new ConflictDetector();
                var s = detector.DetectConflicts(configs, db);
                s.TotalFilesScanned = configs.Count;
                return s;
            });

            await Task.Run(() => ResolveNames(summary, db));

            StatusTextBlock.Text = "Scanning SPID distribution files…";
            var (spidRules, spidFileCount) = await Task.Run(() =>
                new SpidConfigParser().LoadOutfitRulesFromDirectory(Path.Combine(skyrimPath, "Data")));

            if (spidRules.Count > 0)
            {
                StatusTextBlock.Text = $"Found {spidRules.Count} SPID outfit rule(s). Merging conflicts…";
                await Task.Run(() => MergeSpidConflicts(summary, configs, spidRules, db));
            }

            _lastSummary = summary;
            DisplayResults(summary);
            ExportReportButton.IsEnabled = summary.TotalConflicts > 0;

            var spidSuffix = spidFileCount > 0 ? $"  SPID: {spidFileCount} file(s), {spidRules.Count} outfit rule(s)." : string.Empty;
            StatusTextBlock.Text = summary.TotalConflicts == 0
                ? $"Analysis complete — no conflicts in {configs.Count} file(s).  NPC database: {db.RecordCount:N0} record(s).{spidSuffix}"
                : $"Analysis complete — {summary.TotalConflicts} conflict(s) in {configs.Count} file(s).  NPC database: {db.RecordCount:N0} record(s).{spidSuffix}";
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

    private static void ResolveNames(ConflictSummary summary, NpcNameDatabase db)
    {
        foreach (var entry in AllEntries(summary))
        {
            if (entry.NpcRef.RefType != NpcRefType.RecordId) continue;
            entry.ResolvedName     = db.ResolveName(entry.NpcRef.Plugin, entry.NpcRef.FormId);
            entry.ResolvedEditorId = db.ResolveEditorId(entry.NpcRef.Plugin, entry.NpcRef.FormId);
        }
    }

    private static void MergeSpidConflicts(
        ConflictSummary summary,
        List<ModConfiguration> skyPatcherConfigs,
        List<SkyPatcherRule> spidRules,
        NpcNameDatabase db)
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
                        NpcRefType.RecordId => db.ResolveEditorId(npcRef.Plugin, npcRef.FormId),
                        NpcRefType.EditorId => npcRef.Identifier,
                        NpcRefType.Name     => db.FindEditorIdByName(npcRef.Identifier),
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
                    if (!db.IsNpcEditorId(npcRef.Identifier)) continue;
                    eid = npcRef.Identifier;
                }
                else
                {
                    // Reject names that can't be resolved to a known NPC
                    eid = db.FindEditorIdByName(npcRef.Identifier);
                    if (eid == null) continue;
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
        s.AppearanceConflicts.Concat(s.SkinConflicts).Concat(s.OutfitDefaultConflicts);

    private void DisplayResults(ConflictSummary summary)
    {
        var filter = FilterTextBox.Text.Trim();

        FilesScannedText.Text    = summary.TotalFilesScanned.ToString();
        AppearanceCountText.Text = summary.AppearanceConflicts.Count.ToString();
        SkinCountText.Text       = summary.SkinConflicts.Count.ToString();
        OutfitCountText.Text     = summary.OutfitDefaultConflicts.Count.ToString();

        var appearance = FilterEntries(summary.AppearanceConflicts, filter);
        var skin       = FilterEntries(summary.SkinConflicts, filter);
        var outfit     = FilterEntries(summary.OutfitDefaultConflicts, filter);

        PopulateGrid(AppearanceDataGrid, AppearanceEmptyText, AppearanceBadge, AppearanceBadgeText,
                     appearance, RuleType.Appearance);
        PopulateGrid(SkinDataGrid,       SkinEmptyText,       SkinBadge,       SkinBadgeText,
                     skin,        RuleType.Skin);
        PopulateGrid(OutfitDataGrid,     OutfitEmptyText,     OutfitBadge,     OutfitBadgeText,
                     outfit, RuleType.OutfitDefault);

        UpdateRowHeights(appearance.Count > 0, skin.Count > 0, outfit.Count > 0);

        FilterNoMatchText.Visibility =
            !string.IsNullOrEmpty(filter) && appearance.Count == 0 && skin.Count == 0 && outfit.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static List<ConflictEntry> FilterEntries(List<ConflictEntry> entries, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return entries;
        return entries
            .Where(e => e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || e.NpcRef.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_lastSummary is null) return;
        DisplayResults(_lastSummary);
    }

    private static void PopulateGrid(
        DataGrid grid, TextBlock emptyText, Border badge, TextBlock badgeText,
        List<ConflictEntry> entries, RuleType ruleType)
    {
        grid.ItemsSource = entries.Select(e => ConflictDisplayItem.FromEntry(e, ruleType)).ToList();

        var has = entries.Count > 0;
        grid.Visibility      = has ? Visibility.Visible   : Visibility.Collapsed;
        emptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        badge.Visibility     = has ? Visibility.Visible   : Visibility.Collapsed;
        badgeText.Text       = entries.Count.ToString();
    }

    private void UpdateRowHeights(bool hasAppearance, bool hasSkin, bool hasOutfit)
    {
        var large = new System.Windows.GridLength(3, GridUnitType.Star);
        var small = new System.Windows.GridLength(1, GridUnitType.Star);
        AppearanceRow.Height = hasAppearance ? large : small;
        SkinRow.Height       = hasSkin       ? large : small;
        OutfitRow.Height     = hasOutfit     ? large : small;
    }

    private void ClearResults()
    {
        FilesScannedText.Text    = "—";
        AppearanceCountText.Text = "—";
        SkinCountText.Text       = "—";
        OutfitCountText.Text     = "—";

        foreach (var g in new[] { AppearanceDataGrid, SkinDataGrid, OutfitDataGrid })
            g.ItemsSource = null;

        AppearanceEmptyText.Visibility = Visibility.Visible;
        SkinEmptyText.Visibility       = Visibility.Visible;
        OutfitEmptyText.Visibility     = Visibility.Visible;

        AppearanceDataGrid.Visibility = Visibility.Collapsed;
        SkinDataGrid.Visibility       = Visibility.Collapsed;
        OutfitDataGrid.Visibility     = Visibility.Collapsed;

        AppearanceBadge.Visibility = Visibility.Collapsed;
        SkinBadge.Visibility       = Visibility.Collapsed;
        OutfitBadge.Visibility     = Visibility.Collapsed;

        var star = new System.Windows.GridLength(1, GridUnitType.Star);
        AppearanceRow.Height = star;
        SkinRow.Height       = star;
        OutfitRow.Height     = star;

        ExportReportButton.IsEnabled = false;
        _lastSummary = null;
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ConflictDisplayItem item) return;

        var detail = new ConflictDetailWindow(item.Entry, item.RuleType) { Owner = this };
        detail.ShowDialog();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
            File.WriteAllLines(settingsPath, new[] { $"SkyrimPath:{SkyrimPathTextBox.Text}" });
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
            var report    = BuildTextReport(_lastSummary);
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

    private static string BuildTextReport(ConflictSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SkyScope Conflict Report ===");
        sb.AppendLine($"Generated:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Files scanned:   {summary.TotalFilesScanned}");
        sb.AppendLine($"Total conflicts: {summary.TotalConflicts}");
        sb.AppendLine();
        AppendSection(sb, "Appearance Conflicts (copyVisualStyle)",    summary.AppearanceConflicts);
        AppendSection(sb, "Skin Conflicts (skin)",                      summary.SkinConflicts);
        AppendSection(sb, "Default Outfit Conflicts (outfitDefault)",   summary.OutfitDefaultConflicts);
        return sb.ToString();
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
