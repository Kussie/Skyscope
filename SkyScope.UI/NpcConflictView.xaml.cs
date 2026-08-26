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

    private List<NpcConflictViewModel> _allAppearanceNpcs = new();
    private List<NpcConflictViewModel> _allOutfitNpcs     = new();
    private List<NpcConflictViewModel> _allSkinNpcs       = new();
    private List<NpcConflictViewModel> _allOtherNpcs      = new();

    private ConflictSummary?      _lastSummary;
    private ModReferenceLibrary?  _library;

    public HistoryStore? HistoryStore { get; set; }
    public EditOutputOptions OutputOptions { get; set; }

    // Distinct plugin names referenced by appearance-conflict sources, surfaced for the Settings tab.
    public IReadOnlyList<string> AppearancePlugins { get; private set; } = Array.Empty<string>();

    // Plugin -> configured thumbnail folder (from settings.json). Set before Populate.
    public IReadOnlyDictionary<string, string>? ThumbnailDirectories { get; set; }

    // Plugin -> (FormId without extension -> image path), built once per Populate from the
    // configured thumbnail folders so each source can resolve its portrait by O(1) lookup.
    private Dictionary<string, Dictionary<string, string>> _portraitIndex = new(StringComparer.OrdinalIgnoreCase);

    private bool _filterVanilla = true;
    private bool _filterModded  = true;

    private readonly ObservableCollection<NpcConflictViewModel> _appearanceListSource = new();
    private readonly ObservableCollection<NpcConflictViewModel> _outfitListSource     = new();
    private readonly ObservableCollection<NpcConflictViewModel> _skinListSource       = new();
    private readonly ObservableCollection<NpcConflictViewModel> _otherListSource      = new();

    public NpcConflictView()
    {
        InitializeComponent();
        AppearanceList.ItemsSource = _appearanceListSource;
        OutfitList.ItemsSource     = _outfitListSource;
        SkinList.ItemsSource       = _skinListSource;
        OtherList.ItemsSource      = _otherListSource;
    }

    // Raised after a plugin name is copied to the clipboard so the host window can show a toast.
    public event EventHandler<string>? ClipboardCopyRequested;

    // Copies a clicked plugin-name TextBlock's text to the clipboard and signals the host window.
    private void PluginName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Text: { Length: > 0 } text })
            ClipboardHelper.SetTextAsync(text, () => ClipboardCopyRequested?.Invoke(this, "Copied to clipboard"));
    }

    // Opens a larger preview of a clicked portrait thumbnail. No-op when the source has no portrait
    // (the placeholder icon is shown instead and isn't wired to this handler).
    private void Portrait_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NpcTabSourceViewModel { PortraitPath: { Length: > 0 } path } src })
            return;

        var caption = string.IsNullOrEmpty(src.RulePlugin) ? src.FileName : src.RulePlugin;
        var owner   = Window.GetWindow(this);
        try
        {
            var preview = new ImagePreviewWindow(path, caption) { Owner = owner };
            preview.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open preview:\n\n{ex.Message}", "SkyScope — Preview",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Populate(ConflictSummary summary, ModReferenceLibrary? library = null)
    {
        if (library != null) _library = library;
        _lastSummary = summary;

        foreach (var vm in AllNpcVms())
            vm.IsExpanded = false;

        var showLowChance  = ShowLowChanceSpidCheckBox.IsChecked == true;
        var hidePlugins    = HidePluginConflictsCheckBox.IsChecked == true;
        var hidePluginOnly = HidePluginOnlyConflictsCheckBox.IsChecked == true;

        _portraitIndex = BuildPortraitIndex();

        var appearanceDict = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);
        var outfitDict      = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);
        var skinDict        = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);
        var otherDict       = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);

        var appearance = ConflictResolutionHelper.FilterLowChanceSpid(summary.AppearanceConflicts, showLowChance);
        appearance = ConflictResolutionHelper.FilterPluginSources(appearance, hidePlugins);
        appearance = ConflictResolutionHelper.FilterPluginOnlyConflicts(appearance, hidePluginOnly);
        AddGroups(appearanceDict, appearance, RuleType.Appearance, "Appearance", HexBrush(ColorAppearance), _library, _portraitIndex);
        AddGroups(outfitDict, ConflictResolutionHelper.FilterLowChanceSpid(summary.OutfitDefaultConflicts, showLowChance), RuleType.OutfitDefault, "Default Outfit", HexBrush(ColorOutfit), _library, _portraitIndex);
        AddGroups(skinDict, ConflictResolutionHelper.FilterLowChanceSpid(summary.SkinConflicts, showLowChance), RuleType.Skin, "Skin", HexBrush(ColorSkin), _library, _portraitIndex);
        AddGroups(otherDict, summary.SpellConflicts, RuleType.Spell, "Spell", HexBrush(ColorSpell), _library, _portraitIndex);
        AddGroups(otherDict, summary.PerkConflicts,  RuleType.Perk,  "Perk",  HexBrush(ColorPerk),  _library, _portraitIndex);

        _allAppearanceNpcs = SortAndOwn(appearanceDict);
        _allOutfitNpcs     = SortAndOwn(outfitDict);
        _allSkinNpcs       = SortAndOwn(skinDict);
        _allOtherNpcs      = SortAndOwn(otherDict);

        AppearancePlugins = _allAppearanceNpcs
            .SelectMany(vm => vm.Groups)
            .SelectMany(g => g.Sources)
            .Select(s => s.RulePlugin)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    // Sorts a type-specific dict's values and stamps each VM's OwnerList to point at the resulting
    // list, so RemoveSource_Click can later remove an emptied-out NPC from the correct sub-tab list.
    private static List<NpcConflictViewModel> SortAndOwn(Dictionary<string, NpcConflictViewModel> dict)
    {
        var list = dict.Values
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var vm in list) vm.OwnerList = list;
        return list;
    }

    // All NPC VM instances across the four sub-tab lists combined.
    private IEnumerable<NpcConflictViewModel> AllNpcVms() =>
        _allAppearanceNpcs.Concat(_allOutfitNpcs).Concat(_allSkinNpcs).Concat(_allOtherNpcs);

    // All source VMs across the four sub-tab lists combined — used by ApplyEditShift, since one
    // config line can produce sources of different rule types (e.g. copyVisualStyle + skin on the
    // same line) that now live in different sub-tab lists but must still be kept in sync together.
    private IEnumerable<NpcTabSourceViewModel> AllSources() =>
        AllNpcVms().SelectMany(vm => vm.Groups).SelectMany(g => g.Sources);

    private bool HasAnyNpcs() =>
        _allAppearanceNpcs.Count > 0 || _allOutfitNpcs.Count > 0 ||
        _allSkinNpcs.Count > 0       || _allOtherNpcs.Count > 0;

    public void Clear()
    {
        _allAppearanceNpcs.Clear(); _appearanceListSource.Clear();
        _allOutfitNpcs.Clear();     _outfitListSource.Clear();
        _allSkinNpcs.Clear();       _skinListSource.Clear();
        _allOtherNpcs.Clear();      _otherListSource.Clear();

        ResetEmptyState(AppearanceCountText, AppearanceEmptyText);
        ResetEmptyState(OutfitCountText,     OutfitEmptyText);
        ResetEmptyState(SkinCountText,       SkinEmptyText);
        ResetEmptyState(OtherCountText,      OtherEmptyText);
    }

    private static void ResetEmptyState(TextBlock countText, TextBlock emptyText)
    {
        countText.Text       = "";
        emptyText.Text       = "Run analysis to populate this view.";
        emptyText.Visibility = Visibility.Visible;
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

            // Config sources (SPID/SkyPatcher) sort by SkyPatcher's actual breadth-first load order
            // (a folder's files before its subfolders'), then by line; plugin overhaul sources sort
            // by load order. Config sources always rank above plugin sources: SPID and SkyPatcher
            // apply at runtime on top of whatever plugin record wins, so a plugin can be the winner
            // only when there is no config source (a plugin-vs-plugin set), where the highest-load-
            // order plugin wins.
            var configSources = entry.Sources
                .Where(s => s.SourceTool != "Plugin")
                .OrderBy(s => s.FilePath, SkyPatcherLoadOrderComparer.Instance)
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
                    // The plugin this source references (copies-from / outfit / skin / overhaul).
                    // Recorded on the NPC for the "Filter by plugin" search across all conflict types;
                    // only surfaced in the UI ("Plugin:" row / portrait) for Appearance sources.
                    var involvedPlugin = ResolveRulePlugin(src.RuleValue, library);
                    if (!string.IsNullOrEmpty(involvedPlugin)) vm.InvolvedPlugins.Add(involvedPlugin);

                    var plugin = ruleType == RuleType.Appearance ? involvedPlugin : "";
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
                                             ? ResolveRulePortrait(portraitIndex, plugin, src.RuleValue, vm.FormId, library)
                                             : null,
                        SourceTool          = src.SourceTool,
                        SpidChance          = src.SpidChance,
                        SpidNpcIdentifier   = src.SpidNpcIdentifier,
                        IsAdditive          = isAdditive,
                        IsProbabilistic     = allProbabilistic,
                        // "Make Winner" comments out the OTHER config (SPID/SkyPatcher) sources and
                        // never touches plugins. For a config source that means it's useful only
                        // when there's another config source to beat (2+). For a plugin source it
                        // comments out every config source so the plugin wins, which is useful as
                        // soon as there's at least one config source (1+).
                        CanMakeWinner       = src.SourceTool == "Plugin"
                                             ? configSources.Count >= 1
                                             : configSources.Count >= 2,
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

    // Resolves the portrait for an Appearance source. Mugshot packs name images after the FormId of
    // the record the appearance is copied *from* (e.g. t_Amalee_Replacer.esp\00000800.png), so we
    // look up the "copies from" record's FormId in the source plugin's folder first, then fall back
    // to the target NPC's FormId for packs that name images after the NPC instead.
    private static string? ResolveRulePortrait(
        Dictionary<string, Dictionary<string, string>> portraitIndex,
        string plugin, string ruleValue, string npcFormId, ModReferenceLibrary? library)
    {
        if (string.IsNullOrEmpty(plugin)) return null;

        var sourceFormId = ResolveRuleFormId(ruleValue, library);
        return ResolvePortraitPath(portraitIndex, plugin, sourceFormId)
            ?? ResolvePortraitPath(portraitIndex, plugin, npcFormId);
    }

    // Resolves the source record's FormId (8-digit) referenced by an Appearance rule value — the
    // "copies from" record. Inline "Plugin|FormId" / "0xFormId~Plugin" forms carry it directly; a
    // plain EditorId is resolved through the enriched library.
    private static string ResolveRuleFormId(string ruleValue, ModReferenceLibrary? library)
    {
        if (string.IsNullOrEmpty(ruleValue)) return "";
        var token = ruleValue.Split(',')[0].Trim();
        if (token.Length == 0) return "";

        var tildeIdx = token.IndexOf('~');            // SPID form: 0xFormId~Plugin.esp
        if (tildeIdx >= 0) return FormatFormId(token[..tildeIdx].Trim());

        var pipeIdx = token.IndexOf('|');             // SkyPatcher form: Plugin.esp|FormId
        if (pipeIdx >= 0) return FormatFormId(token[(pipeIdx + 1)..].Trim());

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return FormatFormId(token);

        // Plain EditorId — resolve through the library.
        var fid = library?.ResolveFormIdByEditorId(token);
        return string.IsNullOrEmpty(fid) ? "" : FormatFormId(fid);
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
        var search       = SearchBox.Text.Trim();
        var pluginFilter = PluginFilterBox.Text.Trim();

        ApplyFilterToList(_allAppearanceNpcs, _appearanceListSource, AppearanceCountText, AppearanceEmptyText, search, pluginFilter);
        ApplyFilterToList(_allOutfitNpcs,     _outfitListSource,     OutfitCountText,     OutfitEmptyText,     search, pluginFilter);
        ApplyFilterToList(_allSkinNpcs,       _skinListSource,       SkinCountText,       SkinEmptyText,       search, pluginFilter);
        ApplyFilterToList(_allOtherNpcs,      _otherListSource,      OtherCountText,      OtherEmptyText,      search, pluginFilter);
    }

    private void ApplyFilterToList(
        List<NpcConflictViewModel> allNpcs,
        ObservableCollection<NpcConflictViewModel> listSource,
        TextBlock countText, TextBlock emptyText,
        string search, string pluginFilter)
    {
        // No more per-type group filtering (that was the old A/S/O/Sp/P toggle) — each sub-tab's
        // list only ever contains groups relevant to it (Other combines Spell+Perk), so every group
        // on a VM is always shown.
        foreach (var vm in allNpcs)
            vm.FilteredGroups = vm.Groups.ToList();

        var visible = allNpcs.Where(vm =>
        {
            if (vm.IsVanilla  && !_filterVanilla) return false;
            if (!vm.IsVanilla && !_filterModded)  return false;
            // Plugin filter: hide NPCs whose conflicts in THIS sub-tab don't involve the named
            // plugin. Matching NPCs keep all their groups in this sub-tab (doesn't hide non-matches).
            if (!string.IsNullOrEmpty(pluginFilter) &&
                !vm.InvolvedPlugins.Any(p => p.Contains(pluginFilter, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (string.IsNullOrEmpty(search)) return true;
            return vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.SubText.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        SyncList(listSource, visible, vm => vm.IsExpanded = false);

        countText.Text = $"{visible.Count} NPC{(visible.Count == 1 ? "" : "s")}";

        UpdateEmptyState(emptyText, allNpcs.Count > 0, visible.Count,
            "Run analysis to populate this view.",
            "No NPCs match the current filter.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, HasAnyNpcs);

    private void PluginFilterBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, HasAnyNpcs);

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

    private void PluginOnlyFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastSummary is null) return;
        Populate(_lastSummary);
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

        var errors    = new List<string>();
        var commented = new List<NpcTabSourceViewModel>();

        var description = $"{group.Parent?.DisplayName ?? "NPC"} — {group.Label} conflict";
        var tool        = (NpcTabSourceViewModel s) => s.IsSpid ? "SPID" : "SkyPatcher";

        foreach (var src in toComment)
        {
            try
            {
                var editPath = EditOutputPathResolver.ResolveForEdit(src.FilePath, OutputOptions);
                var result = src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier)
                    ? ConflictResolutionHelper.RemoveNpcFromSpidLine(
                        editPath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier,
                        description, tool(src), HistoryStore)
                    : ConflictResolutionHelper.CommentOutLine(
                        editPath, src.LineNumber, src.ConflictLineText,
                        description, tool(src), HistoryStore);

                // Keep the other in-memory sources in the same file valid for the next edit.
                ApplyEditShift(src, src.FilePath, src.LineNumber, result);
                commented.Add(src);
            }
            catch (Exception ex)
            {
                errors.Add($"{src.FileName}: {ex.Message}");
            }
        }

        // Grey out (deactivate) the losing sources whose rules were commented out; the chosen winner
        // stays active. The group stays visible so the resolution is shown in place rather than the
        // conflict vanishing from the list.
        foreach (var src in commented)
            src.IsInactive = true;

        if (commented.Count > 0)
        {
            // The clicked source is now the effective winner (the others are commented out): move
            // the WINNER badge to it, and hide its own "Make Winner" so it can't be re-run against
            // already-commented sources.
            foreach (var s in group.Sources)
                s.IsWinner = ReferenceEquals(s, winner);
            winner.CanMakeWinner = false;
        }

        if (errors.Count > 0)
            MessageBox.Show(
                $"Completed with errors:\n\n{string.Join("\n", errors)}\n\n{commented.Count} file(s) were modified.",
                "SkyScope — Make Winner", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var editPath = EditOutputPathResolver.ResolveForEdit(src.FilePath, OutputOptions);
            var result = src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier)
                ? ConflictResolutionHelper.RemoveNpcFromSpidLine(
                    editPath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier,
                    removeDescription, removeTool, HistoryStore)
                : ConflictResolutionHelper.CommentOutLine(
                    editPath, src.LineNumber, src.ConflictLineText,
                    removeDescription, removeTool, HistoryStore);

            // Keep the other in-memory sources in the same file valid for the next edit.
            ApplyEditShift(src, src.FilePath, src.LineNumber, result);
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
            // The conflict no longer has 2+ sources — drop the whole group.
            var vm = group.Parent;
            if (vm != null)
            {
                vm.Groups.Remove(group);
                if (vm.Groups.Count == 0)
                    vm.OwnerList.Remove(vm);
                else
                    RefreshNpcTypeFlags(vm);
                ApplyFilter();
            }
        }
        else
        {
            // Group survives — re-derive positions, winner badge and Make-Winner availability.
            RefreshGroupSourceStates(group);
        }
    }

    private void ApplyEditShift(NpcTabSourceViewModel edited, string filePath, int atLine, EditResult result)
    {
        if (result.LinesInserted == 0) return;

        foreach (var s in AllSources())
        {
            if (s.SourceTool == "Plugin"
                || !string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (s.LineNumber > atLine)
            {
                s.LineNumber += result.LinesInserted;
            }
            else if (s.LineNumber == atLine && !ReferenceEquals(s, edited))
            {
                s.LineNumber = atLine + result.LinesInserted;
                if (result.RewrittenLine is { } rewritten)
                    s.ConflictLineText = rewritten;
            }
        }
    }

    // Recomputes the NPC header's conflict-type badges (A/S/O/Sp/P) from its remaining groups.
    private static void RefreshNpcTypeFlags(NpcConflictViewModel vm)
    {
        vm.HasAppearance = vm.Groups.Any(g => g.RuleType == RuleType.Appearance);
        vm.HasSkin       = vm.Groups.Any(g => g.RuleType == RuleType.Skin);
        vm.HasOutfit     = vm.Groups.Any(g => g.RuleType == RuleType.OutfitDefault);
        vm.HasSpell      = vm.Groups.Any(g => g.RuleType == RuleType.Spell);
        vm.HasPerk       = vm.Groups.Any(g => g.RuleType == RuleType.Perk);
    }

    // After a source is removed but the group still has 2+ sources, re-derive each source's load
    // position, winner badge, and Make-Winner availability (config sources rank above plugins;
    // Make Winner only makes sense with another config source to resolve against).
    private static void RefreshGroupSourceStates(NpcConflictGroup group)
    {
        var configSources = group.Sources.Where(s => !s.IsPlugin).ToList();
        var winner = configSources.Count > 0
            ? configSources[^1]
            : group.Sources.Count > 0 ? group.Sources[^1] : null;

        for (int i = 0; i < group.Sources.Count; i++)
        {
            var s = group.Sources[i];
            s.LoadPosition  = i + 1;
            s.TotalSources  = group.Sources.Count;
            s.CanMakeWinner = configSources.Count >= 2;
            s.IsWinner      = !s.IsAdditive && !s.IsProbabilistic && winner != null && ReferenceEquals(s, winner);
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

    // Distinct plugin names referenced by any of this NPC's conflict sources (copies-from /
    // outfit / skin / overhaul plugins) — used by the "Filter by plugin" search.
    public HashSet<string> InvolvedPlugins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NpcConflictViewModel> OwnerList { get; set; } = new();

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
    // Settable so the view can bump captured line numbers after an edit inserts a line above them,
    // keeping further edits in the same file valid without re-running the analysis.
    private int _lineNumber;
    public int     LineNumber          { get => _lineNumber; set { _lineNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrecedingLineNumber)); OnPropertyChanged(nameof(FollowingLineNumber)); } }
    // Settable so a source that shared a multi-NPC rule line can be re-pointed to the rewritten line
    // (with fewer NPCs) after another NPC is split out of it, without re-running the analysis.
    private string _conflictLineText = "";
    public string  ConflictLineText    { get => _conflictLineText; set { _conflictLineText = value; OnPropertyChanged(); } }
    public string? PrecedingLine       { get; init; }
    public string? FollowingLine       { get; init; }
    private int _loadPosition;
    public int     LoadPosition        { get => _loadPosition; set { _loadPosition = value; OnPropertyChanged(); } }
    private int _totalSources;
    public int     TotalSources        { get => _totalSources; set { _totalSources = value; OnPropertyChanged(); } }
    public string  RuleValue           { get; init; } = "";
    public string  ResolvedRuleDisplay { get; init; } = "";
    public string  RulePlugin          { get; init; } = "";
    public bool    ShowPortrait        { get; init; }          // reserve the portrait column (Appearance sources)
    public string? PortraitPath        { get; init; }          // set in future to render this source's portrait
    public bool    HasPortrait         => !string.IsNullOrEmpty(PortraitPath);
    public bool    IsAdditive          { get; init; }
    public bool    IsProbabilistic     { get; init; }
    private bool _canMakeWinner = true;
    public bool    CanMakeWinner       { get => _canMakeWinner; set { _canMakeWinner = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowActions)); } }
    public string  SourceTool          { get; init; } = "SkyPatcher";
    public int?    SpidChance          { get; init; }
    public string? SpidNpcIdentifier   { get; init; }
    public NpcConflictGroup? Group     { get; set;  }

    public bool   IsSpid              => SourceTool == "SPID";

    // SPID chance badge — same as the Base Object Swapper tab: "Chance NN%", shown only when the
    // SPID rule carries a chance value.
    public bool   HasSpidChance       => SpidChance.HasValue;
    public string SpidChanceBadgeText => SpidChance.HasValue ? $"Chance {SpidChance.Value}%" : "";

    // Conflict type badge (same style as the Plugin badge), always shown as the last badge.
    public string TypeBadgeText       => SourceTool switch
    {
        "SPID"   => "SPID",
        "Plugin" => "Plugin",
        _        => "Skypatcher"
    };

    // Plugin appearance-overhaul source: read-only (no editable file). SkyPatcher/SPID override
    // it at runtime, so it never carries action buttons and its rule-value/code-context rows
    // (which describe config-file edits) are hidden.
    public bool   IsPlugin            => SourceTool == "Plugin";
    // Config sources always have the action row (Remove / Open File / maybe Make Winner). Plugin
    // sources only show it to offer "Make Winner" (comment out the config rules so the plugin wins).
    public bool   ShowActions         => !IsPlugin || CanMakeWinner;
    public bool   ShowRuleValue       => !IsPlugin;
    public bool   ShowCodeContext     => !IsPlugin;

    public int    PrecedingLineNumber  => LineNumber - 1;
    public int    FollowingLineNumber  => LineNumber + 1;
    public bool   HasPrecedingLine     => !string.IsNullOrEmpty(PrecedingLine);
    public bool   HasFollowingLine     => !string.IsNullOrEmpty(FollowingLine);

    private bool _isWinner;
    public bool IsWinner { get => _isWinner; set { _isWinner = value; OnPropertyChanged(); } }

    // Set on the losing sources after "Make Winner": the rule is now commented out, so the card is
    // greyed and struck through and its action buttons are hidden, while the winner stays active.
    private bool _isInactive;
    public bool IsInactive { get => _isInactive; set { _isInactive = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
