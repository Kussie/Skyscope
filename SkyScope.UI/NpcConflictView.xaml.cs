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
    private const string ColorBtnInactive = "#4C566A";
    private const string ColorBtnActiveFg = "#2E3440";
    private const string ColorBtnInactFg  = "#88C0D0";

    private List<NpcConflictViewModel> _allNpcs = new();
    private ConflictSummary?      _lastSummary;
    private ModReferenceLibrary?  _library;

    public HistoryStore? HistoryStore { get; set; }
    private bool _filterA  = true;
    private bool _filterS  = true;
    private bool _filterO  = true;
    private bool _filterSp = false;
    private bool _filterP  = false;

    private readonly ObservableCollection<NpcConflictViewModel> _npcListSource = new();

    public NpcConflictView()
    {
        InitializeComponent();
        NpcList.ItemsSource = _npcListSource;
    }

    public void Populate(ConflictSummary summary, ModReferenceLibrary? library = null)
    {
        if (library != null) _library = library;
        _lastSummary = summary;

        foreach (var vm in _allNpcs)
            vm.IsExpanded = false;

        var showLowChance = ShowLowChanceSpidCheckBox.IsChecked == true;
        var dict = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);

        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.AppearanceConflicts,    showLowChance), RuleType.Appearance,    "Appearance",     HexBrush(ColorAppearance), null);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.SkinConflicts,          showLowChance), RuleType.Skin,          "Skin",           HexBrush(ColorSkin),       null);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.OutfitDefaultConflicts, showLowChance), RuleType.OutfitDefault, "Default Outfit", HexBrush(ColorOutfit),     null);
        AddGroups(dict, summary.SpellConflicts, RuleType.Spell, "Spell", HexBrush(ColorSpell), _library);
        AddGroups(dict, summary.PerkConflicts,  RuleType.Perk,  "Perk",  HexBrush(ColorPerk),  _library);

        foreach (var vm in dict.Values)
        {
            var last = vm.Groups
                .SelectMany(g => g.Sources)
                .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.LineNumber)
                .LastOrDefault();
            vm.OverallWinnerFileName = last != null
                ? Path.GetFileNameWithoutExtension(last.FilePath)
                : "";
        }

        _allNpcs = dict.Values
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
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
        ModReferenceLibrary? library)
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
                    NormalizedKey = key
                };
                dict[key] = vm;
            }

            var sorted = entry.Sources
                .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.LineNumber)
                .ToList();
            var winner = sorted.Count > 0 ? sorted[^1] : null;

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
                sorted.Select((src, idx) => new NpcTabSourceViewModel
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
                }));

            vm.Groups.Add(group);

            if      (ruleType == RuleType.Appearance)    vm.HasAppearance = true;
            else if (ruleType == RuleType.Skin)          vm.HasSkin       = true;
            else if (ruleType == RuleType.OutfitDefault) vm.HasOutfit     = true;
            else if (ruleType == RuleType.Spell)         vm.HasSpell      = true;
            else if (ruleType == RuleType.Perk)          vm.HasPerk       = true;
        }
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

    private static void UpdateFilterBtn(Button btn, bool active, string activeHex)
    {
        btn.Background = active ? HexBrush(activeHex)      : HexBrush(ColorBtnInactive);
        btn.Foreground = active ? HexBrush(ColorBtnActiveFg) : HexBrush(ColorBtnInactFg);
    }

    private void MakeWinner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not NpcTabSourceViewModel winner) return;
        var group = winner.Group;
        if (group == null) return;

        var toComment = group.Sources
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
    public string NormalizedKey         { get; set; } = "";
    public string OverallWinnerFileName { get; set; } = "";
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
    public string  RuleValueLabel      { get; init; } = "";
    public int     LineNumber          { get; init; }
    public string  ConflictLineText    { get; init; } = "";
    public string? PrecedingLine       { get; init; }
    public string? FollowingLine       { get; init; }
    public int     LoadPosition        { get; init; }
    public int     TotalSources        { get; init; }
    public string  RuleValue           { get; init; } = "";
    public string  ResolvedRuleDisplay { get; init; } = "";
    public bool    IsAdditive          { get; init; }
    public bool    IsProbabilistic     { get; init; }
    public string  SourceTool          { get; init; } = "SkyPatcher";
    public int?    SpidChance          { get; init; }
    public string? SpidNpcIdentifier   { get; init; }
    public NpcConflictGroup? Group     { get; set;  }

    public bool   IsSpid              => SourceTool == "SPID";
    public string SpidBadgeText       => SpidChance.HasValue ? $"SPID  {SpidChance.Value}%" : "SPID";
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
