using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

// Shell + analysis pipeline. Conflict post-processing, report rendering, and settings
// live in the MainWindow.Conflicts / .Report / .Settings partial files.
public partial class MainWindow : Window
{
    private ConflictSummary?    _lastSummary;
    private BosConflictSummary? _lastBosSummary;
    private ScanStats _stats = new();
    private const string SettingsFileName = "skyscope_settings.txt";

    // Per-analysis scan counters surfaced in the Report tab and exported report.
    private sealed class ScanStats
    {
        public int SkyPatcherFilesScanned   { get; set; }
        public int SkyPatcherSupportedFiles { get; set; }
        public int SkyPatcherRuleCount      { get; set; }
        public int SpidFileCount            { get; set; }
        public int SpidSupportedFiles       { get; set; }
        public int SpidRuleCount            { get; set; }
        public int BosFileCount             { get; set; }
        public int BosSupportedFiles        { get; set; }
        public int BosRuleCount             { get; set; }
        public int DbRecordCount            { get; set; }
    }

    private readonly HistoryStore _historyStore = new();

    public MainWindow()
    {
        InitializeComponent();
        EnsureAppFilesExist();
        _historyStore.Load();
        NpcConflictViewControl.HistoryStore = _historyStore;
        NpcConflictViewControl.ClipboardCopyRequested += (_, message) => ShowToast(message);
        BosConflictViewControl.HistoryStore = _historyStore;
        HistoryViewControl.Refresh(_historyStore);
        LoadSettings();
        LoadAppSettings();
        LoadVersion();
    }

    private void LoadVersion()
    {
        try
        {
            var path    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            var version = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            VersionTextBlock.Text = string.IsNullOrEmpty(version) ? "development version" : version;
        }
        catch
        {
            VersionTextBlock.Text = "development version";
        }
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
            SkyrimPathTextBox.Text = NormaliseSkyrimPath(dialog.SelectedPath);
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var skyrimPath = NormaliseSkyrimPath(SkyrimPathTextBox.Text?.Trim() ?? "");

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
        ReportTab.IsEnabled     = false;
        NpcTab.IsEnabled        = false;
        BosTab.IsEnabled        = false;
        MainTabControl.SelectedIndex = 0;
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt"), "", Encoding.UTF8); }
        catch { }

        try
        {
            // ── Step 1: Parse all config files (no plugin I/O) ─────────────

            StatusTextBlock.Text = "Scanning SkyPatcher configs…";

            List<ModConfiguration> configs        = [];
            int                    spFilesScanned = 0;
            List<string>           spErrors       = [];
            try
            {
                (configs, spFilesScanned, spErrors) = await Task.Run(() =>
                    new SkyPatcherConfigParser().LoadConfigurationsFromSkyrimDirectory(skyrimPath));
            }
            catch (DirectoryNotFoundException ex)
            {
                spErrors = [ex.Message];
                // SkyPatcher not installed — continue so SPID/BOS analysis still runs
            }


            StatusTextBlock.Text = "Scanning SPID distribution files…";
            var (spidRules, spidFileCount, spidErrors) = await Task.Run(() =>
                new SpidConfigParser().LoadDistributionRulesFromDirectory(Path.Combine(skyrimPath, "Data")));

            StatusTextBlock.Text = "Scanning Base Object Swapper files…";
            var (bosRules, bosFileCount, bosErrors) = await Task.Run(() =>
                new BosConfigParser().LoadSwapRulesFromDirectory(Path.Combine(skyrimPath, "Data")));

            // ── Step 2: Build reference library from parsed rules ───────────

            StatusTextBlock.Text = "Building reference library…";
            var loadedPlugins = await Task.Run(() => PluginPathResolver.GetOrderedPluginNames(skyrimPath));
            var library       = new ModReferenceLibrary();
            library.SetLoadedPlugins(loadedPlugins);
            await Task.Run(() => new ReferenceExtractor().Extract(library, configs, spidRules, bosRules));

            // ── Step 3: Enrich library from plugin files ────────────────────

            var progress = new Progress<string>(msg => StatusTextBlock.Text = msg);
            var ignoredPlugins = _appSettings.IgnoredAppearancePlugins;
            await Task.Run(() => new PluginEnricher().Enrich(library, skyrimPath, progress, ignoredPlugins));
            _stats.DbRecordCount = library.NpcRecordCount;

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
                s.TotalFilesScanned = spFilesScanned;
                return s;
            });

            await Task.Run(() => ResolveNames(summary, library));

            var spidOutfitRules = spidRules.Where(r => r.RuleType == RuleType.OutfitDefault).ToList();
            if (spidOutfitRules.Count > 0)
            {
                StatusTextBlock.Text = $"Found {spidOutfitRules.Count} SPID outfit rule(s). Merging conflicts…";
                await Task.Run(() => MergeSpidConflicts(summary, configs, spidOutfitRules, library));
            }

            // Fill in full display names for EditorId-keyed / SPID-merged conflicts.
            await Task.Run(() => FillResolvedNames(summary, library));

            // Collapse duplicate entries for same-named NPC records targeted by name filters.
            await Task.Run(() => DeduplicateConflicts(summary));

            var bosSummary = await Task.Run(() =>
                new BosConflictDetector().DetectConflicts(bosRules, library));
            bosSummary.FilesScanned = bosFileCount;

            _stats.SkyPatcherFilesScanned  = spFilesScanned;
            _stats.SkyPatcherSupportedFiles = configs.Count;
            _stats.SkyPatcherRuleCount      = configs.Sum(c => c.Rules.Count);
            _stats.SpidFileCount            = spidFileCount;
            _stats.SpidSupportedFiles       = spidRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _stats.SpidRuleCount            = spidRules.Count;
            _stats.BosFileCount             = bosFileCount;
            _stats.BosSupportedFiles        = bosRules.Select(r => r.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _stats.BosRuleCount             = bosRules.Count;
            _lastSummary             = summary;
            _lastBosSummary          = bosSummary;
            ReportTab.IsEnabled  = true;
            NpcTab.IsEnabled     = true;
            BosTab.IsEnabled     = true;
            MainTabControl.SelectedIndex = 1;
            DisplayResults(summary, bosSummary);

            WriteAnalysisLog(skyrimPath, configs, spFilesScanned, spErrors, spidRules, spidFileCount, spidErrors, bosRules, bosFileCount, bosErrors);
            NpcConflictViewControl.ThumbnailDirectories = _appSettings.PluginThumbnailDirectories;
            NpcConflictViewControl.Populate(summary, library);
            MergeAppearancePlugins(NpcConflictViewControl.AppearancePlugins);
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
            var skyPatcherPart = configs.Count > 0
                ? $"{configs.Count} SkyPatcher file(s)"
                : spFilesScanned > 0
                    ? $"{spFilesScanned} SkyPatcher file(s) scanned (no trackable NPC rules)"
                    : "no SkyPatcher NPC rule files found";
            StatusTextBlock.Text = totalConflicts == 0
                ? $"Analysis complete — no conflicts.  {skyPatcherPart}.  NPC database: {library.NpcRecordCount:N0} record(s).{spidSuffix}{bosSuffix}"
                : $"Analysis complete — {totalConflicts} conflict(s) in {configs.Count} SkyPatcher file(s).  NPC database: {library.NpcRecordCount:N0} record(s).{spidSuffix}{bosSuffix}";

            ShowToast("Analysis Complete");
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
}
