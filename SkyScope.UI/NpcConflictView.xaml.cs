using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public partial class NpcConflictView : ConflictViewBase
{
    private const string ColorAppearance  = "#EBCB8B";
    private const string ColorSkin        = "#D08770";
    private const string ColorOutfit      = "#B48EAD";
    private const string ColorSpell       = "#A3BE8C";
    private const string ColorPerk        = "#BF616A";
    private const string ColorBase        = "#88C0D0";
    private const string ColorMods        = "#5E81AC";
    private const string ColorModsFg      = "#ECEFF4";
    private const string ColorBtnInactive = "#4C566A";
    private const string ColorBtnActiveFg = "#2E3440";
    private const string ColorBtnInactFg  = "#88C0D0";

    private List<NpcConflictViewModel> _allNpcs = new();
    private ConflictSummary?      _lastSummary;
    private ModReferenceLibrary?  _library;

    public HistoryStore? HistoryStore { get; set; }

    // Distinct plugin names referenced by appearance-conflict sources, surfaced for the Settings tab.
    public IReadOnlyList<string> AppearancePlugins { get; private set; } = Array.Empty<string>();

    // Plugin -> configured thumbnail folder (from settings.json). Set before Populate.
    public IReadOnlyDictionary<string, string>? ThumbnailDirectories { get; set; }

    // Plugin -> (FormId without extension -> image path), built once per Populate from the
    // configured thumbnail folders so each source can resolve its portrait by O(1) lookup.
    private Dictionary<string, Dictionary<string, string>> _portraitIndex = new(StringComparer.OrdinalIgnoreCase);

    private bool _filterA  = true;
    private bool _filterS  = true;
    private bool _filterO  = true;
    private bool _filterSp = false;
    private bool _filterP  = false;
    private bool _filterVanilla = true;
    private bool _filterModded  = true;

    private readonly ObservableCollection<NpcConflictViewModel> _npcListSource = new();

    public NpcConflictView()
    {
        InitializeComponent();
        NpcList.ItemsSource = _npcListSource;
    }

    // Raised after a plugin name is copied to the clipboard so the host window can show a toast.
    public event EventHandler<string>? ClipboardCopyRequested;

    // Copies a clicked plugin-name TextBlock's text to the clipboard and signals the host window.
    private void PluginName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Text: { Length: > 0 } text })
            ClipboardHelper.SetTextAsync(text, () => ClipboardCopyRequested?.Invoke(this, "Copied to clipboard"));
    }

    public void Populate(ConflictSummary summary, ModReferenceLibrary? library = null)
    {
        if (library != null) _library = library;
        _lastSummary = summary;

        foreach (var vm in _allNpcs)
            vm.IsExpanded = false;

        var showLowChance = ShowLowChanceSpidCheckBox.IsChecked == true;
        var hidePlugins   = HidePluginConflictsCheckBox.IsChecked == true;
        var dict = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);

        _portraitIndex = BuildPortraitIndex();

        var appearance = ConflictResolutionHelper.FilterLowChanceSpid(summary.AppearanceConflicts, showLowChance);
        appearance = ConflictResolutionHelper.FilterPluginSources(appearance, hidePlugins);
        AddGroups(dict, appearance, RuleType.Appearance, "Appearance", HexBrush(ColorAppearance), _library, _portraitIndex);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.SkinConflicts,          showLowChance), RuleType.Skin,          "Skin",           HexBrush(ColorSkin),       _library, _portraitIndex);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.OutfitDefaultConflicts, showLowChance), RuleType.OutfitDefault, "Default Outfit", HexBrush(ColorOutfit),     _library, _portraitIndex);
        AddGroups(dict, summary.SpellConflicts, RuleType.Spell, "Spell", HexBrush(ColorSpell), _library, _portraitIndex);
        AddGroups(dict, summary.PerkConflicts,  RuleType.Perk,  "Perk",  HexBrush(ColorPerk),  _library, _portraitIndex);

        _allNpcs = dict.Values
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppearancePlugins = _allNpcs
            .SelectMany(vm => vm.Groups)
            .Where(g => g.RuleType == RuleType.Appearance)
            .SelectMany(g => g.Sources)
            .Select(s => s.RulePlugin)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    public void Clear()
    {
        _allNpcs.Clear();
        _npcListSource.Clear();
        NpcCountText.Text    = "";
        EmptyText.Text       = "Run analysis to populate this view.";
        EmptyText.Visibility = Visibility.Visible;
    }

    private static void AddGroups(
        Dictionary<string, NpcConflictViewModel> dict,
        List<ConflictEntry> entries,
        RuleType ruleType,
        string label,
        SolidColorBrush badgeBrush,
        ModReferenceLibrary? library,
        Dictionary<string, Dictionary<string, string>> portraitIndex)
    {
        foreach (var entry in entries)
        {
            var key = !string.IsNullOrEmpty(entry.ResolvedEditorId)
                ? entry.ResolvedEditorId
                : entry.NpcRef.NormalizedKey;

            if (!dict.TryGetValue(key, out var vm))
            {
                vm = new NpcConflictViewModel
                {
                    DisplayName   = entry.DisplayName,
                    SubText       = BuildSubText(entry),
                    FormId        = BuildFormId(entry, library),
                    NormalizedKey = key,
                    IsVanilla     = ResolveIsVanilla(entry, library)
                };
                dict[key] = vm;
            }

            // Config sources (SPID/SkyPatcher) sort by file then line as before; plugin overhaul
            // sources sort by load order. Config sources always rank above plugin sources: SPID and
            // SkyPatcher apply at runtime on top of whatever plugin record wins, so a plugin can be
            // the winner only when there is no config source (a plugin-vs-plugin set), where the
            // highest-load-order plugin wins.
            var configSources = entry.Sources
                .Where(s => s.SourceTool != "Plugin")
                .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.LineNumber)
                .ToList();
            var pluginSources = entry.Sources
                .Where(s => s.SourceTool == "Plugin")
                .OrderBy(s => s.LoadOrderIndex ?? int.MaxValue)
                .ToList();
            var sorted = configSources.Concat(pluginSources).ToList();

            var winner = configSources.Count > 0 ? configSources[^1]
                       : pluginSources.Count > 0 ? pluginSources[^1]
                       : null;

            // Outfit conflicts where every source is a probabilistic SPID rule (chance < 100%)
            // have no clear winner — treat like additive rules: show Remove, hide Make Winner.
            bool allProbabilistic = ruleType == RuleType.OutfitDefault &&
                                    sorted.Count > 0 &&
                                    sorted.All(s => s.SpidChance.HasValue && s.SpidChance.Value < 100);
            if (allProbabilistic) winner = null;

            bool isAdditive = ruleType is RuleType.Spell or RuleType.Perk;
            var group = new NpcConflictGroup
            {
                RuleType   = ruleType,
                Label      = label,
                BadgeBrush = badgeBrush,
                Parent     = vm
            };

            group.Sources = new ObservableCollection<NpcTabSourceViewModel>(
                sorted.Select((src, idx) =>
                {
                    var plugin = ruleType == RuleType.Appearance
                        ? ResolveRulePlugin(src.RuleValue, library)
                        : "";
                    return new NpcTabSourceViewModel
                    {
                        FileName            = Path.GetFileName(src.FilePath),
                        FilePath            = src.FilePath,
                        LineNumber          = src.LineNumber,
                        ConflictLineText    = src.ConflictLine,
                        PrecedingLine       = src.PrecedingLine,
                        FollowingLine       = src.FollowingLine,
                        LoadPosition        = idx + 1,
                        TotalSources        = sorted.Count,
                        RuleValue           = src.RuleValue,
                        ResolvedRuleDisplay = library != null
                                             ? library.ResolveRuleValue(src.RuleValue)
                                             : src.RuleValue,
                        RuleValueLabel      = ruleType switch
                        {
                            RuleType.Appearance    => "Copies from:",
                            RuleType.Skin          => "Skin:",
                            RuleType.OutfitDefault => "Outfit:",
                            RuleType.Spell         => "Spell:",
                            RuleType.Perk          => "Perk:",
                            _                      => "Value:"
                        },
                        RulePlugin          = plugin,
                        ShowPortrait        = ruleType == RuleType.Appearance,
                        PortraitPath        = ruleType == RuleType.Appearance
                                             ? ResolvePortraitPath(portraitIndex, plugin, vm.FormId)
                                             : null,
                        SourceTool          = src.SourceTool,
                        SpidChance          = src.SpidChance,
                        SpidNpcIdentifier   = src.SpidNpcIdentifier,
                        IsAdditive          = isAdditive,
                        IsProbabilistic     = allProbabilistic,
                        IsWinner            = !isAdditive && !allProbabilistic &&
                                             winner != null &&
                                             string.Equals(src.FilePath, winner.FilePath, StringComparison.OrdinalIgnoreCase) &&
                                             src.LineNumber == winner.LineNumber,
                        Group               = group
                    };
                }));

            vm.Groups.Add(group);

            if      (ruleType == RuleType.Appearance)    vm.HasAppearance = true;
            else if (ruleType == RuleType.Skin)          vm.HasSkin       = true;
            else if (ruleType == RuleType.OutfitDefault) vm.HasOutfit     = true;
            else if (ruleType == RuleType.Spell)         vm.HasSpell      = true;
            else if (ruleType == RuleType.Perk)          vm.HasPerk       = true;
        }
    }

    // Resolves the source plugin for an Appearance rule value: the inline plugin token when present
    // ("Plugin.esp|FormId" / "0xFormId~Plugin.esp"), otherwise the owning plugin of the referenced
    // EditorId looked up from the enriched library.
    private static string ResolveRulePlugin(string ruleValue, ModReferenceLibrary? library)
    {
        var inline = ExtractPlugin(ruleValue);
        if (!string.IsNullOrEmpty(inline)) return inline;

        if (library == null || string.IsNullOrEmpty(ruleValue)) return "";

        var token = ruleValue.Split(',')[0].Trim();
        if (token.Length == 0 || token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return "";

        return library.ResolvePluginByEditorId(token) ?? "";
    }

    // Re-resolves portraits on an already-populated view (e.g. after thumbnail folders change in
    // Settings). No-op until an analysis has populated the view.
    public void RefreshPortraits()
    {
        if (_lastSummary != null) Populate(_lastSummary);
    }

    // Indexes the configured thumbnail folders: for each plugin with a valid folder, maps every
    // png/jpg/jpeg file (by name without extension) to its full path, scanning subdirectories.
    private Dictionary<string, Dictionary<string, string>> BuildPortraitIndex()
    {
        var index = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (ThumbnailDirectories == null) return index;

        foreach (var (plugin, dir) in ThumbnailDirectories)
        {
            if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".png",  StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".jpg",  StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = Path.GetFileNameWithoutExtension(file);
                    map.TryAdd(name, file);
                }
            }
            catch { /* unreadable folder — leave whatever was indexed */ }

            index[plugin] = map;
        }

        return index;
    }

    // Looks up the portrait for a source by its plugin's configured folder and the NPC FormId
    // (e.g. "000135E6" → "…\000135E6.png"). Returns null when unset or not found.
    private static string? ResolvePortraitPath(
        Dictionary<string, Dictionary<string, string>> portraitIndex, string plugin, string formId)
    {
        if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(formId)) return null;
        return portraitIndex.TryGetValue(plugin, out var map) && map.TryGetValue(formId, out var path)
            ? path
            : null;
    }

    // Extracts the plugin name from a rule's source reference: "Plugin.esp|FormId" (SkyPatcher)
    // or "0xFormId~Plugin.esp" (SPID). Returns "" for plain EditorId references (no plugin).
    private static string ExtractPlugin(string ruleValue)
    {
        if (string.IsNullOrEmpty(ruleValue)) return "";

        var token = ruleValue.Split(',')[0].Trim();

        var tildeIdx = token.IndexOf('~');
        if (tildeIdx >= 0) return token[(tildeIdx + 1)..].Trim();

        var pipeIdx = token.IndexOf('|');
        if (pipeIdx >= 0) return token[..pipeIdx].Trim();

        return "";
    }

    // Resolves the NPC's local FormId for display: the enriched record's id (looked up by EditorId),
    // falling back to a direct RecordId reference, then to the rule's raw identifier when it
    // happens to match a known NPC EditorId (vanilla NPCs like Delphine/Lydia have EditorId ==
    // display name, so a Name-typed filter still resolves). Returns "" when unknown.
    private static string BuildFormId(ConflictEntry entry, ModReferenceLibrary? library)
    {
        if (!string.IsNullOrEmpty(entry.ResolvedEditorId) && library != null)
        {
            var fid = library.ResolveFormIdByEditorId(entry.ResolvedEditorId);
            if (!string.IsNullOrEmpty(fid)) return FormatFormId(fid);
        }

        if (entry.NpcRef.RefType == NpcRefType.RecordId)
            return FormatFormId(entry.NpcRef.FormId);

        if (library != null
            && entry.NpcRef.RefType is NpcRefType.EditorId or NpcRefType.Name
            && !string.IsNullOrEmpty(entry.NpcRef.Identifier))
        {
            var fid = library.ResolveFormIdByEditorId(entry.NpcRef.Identifier);
            if (!string.IsNullOrEmpty(fid)) return FormatFormId(fid);
        }

        return "";
    }

    // Determines whether a conflict's target NPC originates from a vanilla plugin. Records keyed
    // by Plugin|FormId answer directly; EditorId/Name refs resolve through the library's
    // EditorId→Plugin index. Unknown origin (e.g. plugin not loaded) is treated as modded so
    // custom NPCs aren't accidentally hidden when the user is filtering to "Modded".
    private static bool ResolveIsVanilla(ConflictEntry entry, ModReferenceLibrary? library)
    {
        if (entry.NpcRef.RefType == NpcRefType.RecordId)
            return VanillaPlugins.IsVanilla(entry.NpcRef.Plugin);

        if (library == null) return false;

        var eid = !string.IsNullOrEmpty(entry.ResolvedEditorId)
            ? entry.ResolvedEditorId
            : entry.NpcRef.RefType is NpcRefType.EditorId or NpcRefType.Name
                ? entry.NpcRef.Identifier
                : null;

        if (string.IsNullOrEmpty(eid)) return false;
        return VanillaPlugins.IsVanilla(library.ResolvePluginByEditorId(eid));
    }

    // Normalises a hex FormId to an 8-digit uppercase id, master byte 00 (e.g. "135E6" → "000135E6").
    private static string FormatFormId(string? rawHex)
    {
        if (string.IsNullOrEmpty(rawHex)) return "";
        var hex = rawHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawHex[2..] : rawHex;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return "";
        return (v & 0x00FFFFFF).ToString("X8");
    }

    private static string BuildSubText(ConflictEntry entry)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(entry.ResolvedEditorId))
            parts.Add(entry.ResolvedEditorId);
        if (!string.IsNullOrEmpty(entry.ResolvedName)
            && !string.Equals(entry.ResolvedName, entry.ResolvedEditorId, StringComparison.OrdinalIgnoreCase))
            parts.Add(entry.ResolvedName);
        return string.Join("  ·  ", parts);
    }

    private static SolidColorBrush HexBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();

        foreach (var vm in _allNpcs)
        {
            vm.FilteredGroups = vm.Groups.Where(g =>
                (g.RuleType == RuleType.Appearance    && _filterA)  ||
                (g.RuleType == RuleType.Skin          && _filterS)  ||
                (g.RuleType == RuleType.OutfitDefault && _filterO)  ||
                (g.RuleType == RuleType.Spell         && _filterSp) ||
                (g.RuleType == RuleType.Perk          && _filterP)
            ).ToList();
        }

        var visible = _allNpcs.Where(vm =>
        {
            if (vm.FilteredGroups.Count == 0) return false;
            if (vm.IsVanilla  && !_filterVanilla) return false;
            if (!vm.IsVanilla && !_filterModded)  return false;
            if (string.IsNullOrEmpty(search)) return true;
            return vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.SubText.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        SyncList(_npcListSource, visible, vm => vm.IsExpanded = false);

        NpcCountText.Text = $"{visible.Count} NPC{(visible.Count == 1 ? "" : "s")}";

        UpdateEmptyState(EmptyText, _allNpcs.Count > 0, visible.Count,
            "Run analysis to populate this view.",
            "No NPCs match the current filter.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, () => _allNpcs.Count > 0);

    private void FilterA_Click(object sender, RoutedEventArgs e)  => ToggleFilter(ref _filterA,  FilterAButton,  ColorAppearance);
    private void FilterS_Click(object sender, RoutedEventArgs e)  => ToggleFilter(ref _filterS,  FilterSButton,  ColorSkin);
    private void FilterO_Click(object sender, RoutedEventArgs e)  => ToggleFilter(ref _filterO,  FilterOButton,  ColorOutfit);
    private void FilterSp_Click(object sender, RoutedEventArgs e) => ToggleFilter(ref _filterSp, FilterSpButton, ColorSpell);
    private void FilterP_Click(object sender, RoutedEventArgs e)  => ToggleFilter(ref _filterP,  FilterPButton,  ColorPerk);

    private void ToggleFilter(ref bool flag, Button btn, string activeHex)
    {
        flag = !flag;
        UpdateFilterBtn(btn, flag, activeHex);
        ApplyFilter();
    }

    private void SpidFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastSummary is null) return;
        Populate(_lastSummary);
    }

    private void PluginFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastSummary is null) return;
        Populate(_lastSummary);
    }

    private void FilterBase_Click(object sender, RoutedEventArgs e)
    {
        _filterVanilla = !_filterVanilla;
        UpdateFilterBtn(FilterBaseButton, _filterVanilla, ColorBase);
        ApplyFilter();
    }

    private void FilterMods_Click(object sender, RoutedEventArgs e)
    {
        _filterModded = !_filterModded;
        UpdateFilterBtn(FilterModsButton, _filterModded, ColorMods, ColorModsFg);
        ApplyFilter();
    }

    private static void UpdateFilterBtn(Button btn, bool active, string activeHex, string? activeFgHex = null)
    {
        btn.Background = active ? HexBrush(activeHex)                          : HexBrush(ColorBtnInactive);
        btn.Foreground = active ? HexBrush(activeFgHex ?? ColorBtnActiveFg)    : HexBrush(ColorBtnInactFg);
    }

    private void MakeWinner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not NpcTabSourceViewModel winner) return;
        var group = winner.Group;
        if (group == null) return;

        // Plugin overhaul sources are never edited: they have no config line to comment, and
        // SkyPatcher/SPID override the plugin record at runtime anyway. Skip them here so making a
        // config source the winner only touches the other SPID/SkyPatcher sources.
        var toComment = group.Sources
            .Where(s => s.SourceTool != "Plugin")
            .Where(s => !(string.Equals(s.FilePath, winner.FilePath, StringComparison.OrdinalIgnoreCase)
                          && s.LineNumber == winner.LineNumber))
            .ToList();

        var errors   = new List<string>();
        int modified = 0;

        var description = $"{group.Parent?.DisplayName ?? "NPC"} — {group.Label} conflict";
        var tool        = (NpcTabSourceViewModel s) => s.IsSpid ? "SPID" : "SkyPatcher";

        foreach (var src in toComment)
        {
            try
            {
                if (src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier))
                    ConflictResolutionHelper.RemoveNpcFromSpidLine(
                        src.FilePath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier,
                        description, tool(src), HistoryStore);
                else
                    ConflictResolutionHelper.CommentOutLine(
                        src.FilePath, src.LineNumber, src.ConflictLineText,
                        description, tool(src), HistoryStore);
                modified++;
            }
            catch (Exception ex)
            {
                errors.Add($"{src.FileName}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"Completed with errors:\n\n{string.Join("\n", errors)}\n\n{modified} file(s) were modified.",
                "SkyScope — Make Winner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(
                $"Done — commented out conflicting rules in {modified} file(s).",
                "SkyScope — Make Winner", MessageBoxButton.OK, MessageBoxImage.Information);

            var vm = group.Parent;
            vm?.Groups.Remove(group);

            if (vm != null && vm.Groups.Count == 0)
            {
                _allNpcs.Remove(vm);
                ApplyFilter();
            }
            else if (vm != null)
            {
                vm.HasAppearance = vm.Groups.Any(g => g.RuleType == RuleType.Appearance);
                vm.HasSkin       = vm.Groups.Any(g => g.RuleType == RuleType.Skin);
                vm.HasOutfit     = vm.Groups.Any(g => g.RuleType == RuleType.OutfitDefault);
                vm.HasSpell      = vm.Groups.Any(g => g.RuleType == RuleType.Spell);
                vm.HasPerk       = vm.Groups.Any(g => g.RuleType == RuleType.Perk);
            }
        }
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not NpcTabSourceViewModel src) return;
        if (src.IsPlugin) return;  // plugin overhaul sources are read-only (no editable file)
        var group = src.Group;
        if (group == null) return;

        var removeDescription = $"{group.Parent?.DisplayName ?? "NPC"} — {group.Label} conflict";
        var removeTool        = src.IsSpid ? "SPID" : "SkyPatcher";

        try
        {
            if (src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier))
                ConflictResolutionHelper.RemoveNpcFromSpidLine(
                    src.FilePath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier,
                    removeDescription, removeTool, HistoryStore);
            else
                ConflictResolutionHelper.CommentOutLine(
                    src.FilePath, src.LineNumber, src.ConflictLineText,
                    removeDescription, removeTool, HistoryStore);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not modify file:\n\n{ex.Message}", "SkyScope — Remove",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        group.Sources.Remove(src);

        if (group.Sources.Count < 2)
        {
            var vm = group.Parent;
            if (vm != null)
            {
                vm.Groups.Remove(group);
                if (vm.Groups.Count == 0)
                {
                    _allNpcs.Remove(vm);
                    ApplyFilter();
                }
                else
                {
                    vm.HasSpell = vm.Groups.Any(g => g.RuleType == RuleType.Spell);
                    vm.HasPerk  = vm.Groups.Any(g => g.RuleType == RuleType.Perk);
                    ApplyFilter();
                }
            }
        }
    }
}

public class NpcConflictViewModel : INotifyPropertyChanged, IConflictItemVm
{
    public string DisplayName           { get; set; } = "";
    public string SubText               { get; set; } = "";
    public string FormId                { get; set; } = "";
    public string NormalizedKey         { get; set; } = "";
    public bool   IsVanilla             { get; set; }
    public ObservableCollection<NpcConflictGroup> Groups { get; } = new();

    private bool _hasAppearance;
    public bool HasAppearance { get => _hasAppearance; set { _hasAppearance = value; OnPropertyChanged(); } }

    private bool _hasSkin;
    public bool HasSkin { get => _hasSkin; set { _hasSkin = value; OnPropertyChanged(); } }

    private bool _hasOutfit;
    public bool HasOutfit { get => _hasOutfit; set { _hasOutfit = value; OnPropertyChanged(); } }

    private bool _hasSpell;
    public bool HasSpell { get => _hasSpell; set { _hasSpell = value; OnPropertyChanged(); } }

    private bool _hasPerk;
    public bool HasPerk { get => _hasPerk; set { _hasPerk = value; OnPropertyChanged(); } }

    private List<NpcConflictGroup> _filteredGroups = new();
    public List<NpcConflictGroup> FilteredGroups
    {
        get => _filteredGroups;
        set { _filteredGroups = value; OnPropertyChanged(); }
    }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class NpcConflictGroup
{
    public RuleType                                    RuleType   { get; init; }
    public string                                      Label      { get; init; } = "";
    public SolidColorBrush                             BadgeBrush { get; init; } = Brushes.Gray;
    public ObservableCollection<NpcTabSourceViewModel> Sources    { get; set;  } = new();
    public NpcConflictViewModel?                       Parent     { get; set;  }
}

public class NpcTabSourceViewModel : INotifyPropertyChanged, IConflictSourceVm
{
    public string  FileName            { get; init; } = "";
    public string  FilePath            { get; init; } = "";
    public string  DisplayPath         => ConflictViewBase.ToSkyrimRelativePath(FilePath);
    public string  RuleValueLabel      { get; init; } = "";
    public int     LineNumber          { get; init; }
    public string  ConflictLineText    { get; init; } = "";
    public string? PrecedingLine       { get; init; }
    public string? FollowingLine       { get; init; }
    public int     LoadPosition        { get; init; }
    public int     TotalSources        { get; init; }
    public string  RuleValue           { get; init; } = "";
    public string  ResolvedRuleDisplay { get; init; } = "";
    public string  RulePlugin          { get; init; } = "";
    public bool    ShowPortrait        { get; init; }          // reserve the portrait column (Appearance sources)
    public string? PortraitPath        { get; init; }          // set in future to render this source's portrait
    public bool    HasPortrait         => !string.IsNullOrEmpty(PortraitPath);
    public bool    IsAdditive          { get; init; }
    public bool    IsProbabilistic     { get; init; }
    public string  SourceTool          { get; init; } = "SkyPatcher";
    public int?    SpidChance          { get; init; }
    public string? SpidNpcIdentifier   { get; init; }
    public NpcConflictGroup? Group     { get; set;  }

    public bool   IsSpid              => SourceTool == "SPID";
    public string SpidBadgeText       => SpidChance.HasValue ? $"SPID  {SpidChance.Value}%" : "SPID";

    // Plugin appearance-overhaul source: read-only (no editable file). SkyPatcher/SPID override
    // it at runtime, so it never carries action buttons and its rule-value/code-context rows
    // (which describe config-file edits) are hidden.
    public bool   IsPlugin            => SourceTool == "Plugin";
    public bool   ShowActions         => !IsPlugin;
    public bool   ShowRuleValue       => !IsPlugin;
    public bool   ShowCodeContext     => !IsPlugin;

    public int    PrecedingLineNumber  => LineNumber - 1;
    public int    FollowingLineNumber  => LineNumber + 1;
    public bool   HasPrecedingLine     => !string.IsNullOrEmpty(PrecedingLine);
    public bool   HasFollowingLine     => !string.IsNullOrEmpty(FollowingLine);

    private bool _isWinner;
    public bool IsWinner { get => _isWinner; set { _isWinner = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
