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
using System.Windows.Threading;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public partial class NpcConflictView : UserControl
{
    private List<NpcConflictViewModel> _allNpcs = new();
    private ConflictSummary?  _lastSummary;
    private FormNameDatabase? _formDb;
    private bool _filterA  = true;
    private bool _filterS  = true;
    private bool _filterO  = true;
    private bool _filterSp = true;
    private bool _filterP  = true;
    private DispatcherTimer? _searchDebounce;

    // Persistent source — never replaced, only updated in-place so WPF containers
    // are not torn down while accordion animations may be running.
    private readonly ObservableCollection<NpcConflictViewModel> _npcListSource = new();

    public NpcConflictView()
    {
        InitializeComponent();
        NpcList.ItemsSource = _npcListSource;
    }

    public void Populate(ConflictSummary summary, FormNameDatabase? formDb = null)
    {
        if (formDb != null) _formDb = formDb;
        _lastSummary = summary;

        foreach (var vm in _allNpcs)
            vm.IsExpanded = false;

        var showLowChance = ShowLowChanceSpidCheckBox.IsChecked == true;
        var dict = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);

        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.AppearanceConflicts,    showLowChance), RuleType.Appearance,    "Appearance",     HexBrush("#EBCB8B"), null);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.SkinConflicts,          showLowChance), RuleType.Skin,          "Skin",           HexBrush("#D08770"), null);
        AddGroups(dict, ConflictResolutionHelper.FilterLowChanceSpid(summary.OutfitDefaultConflicts, showLowChance), RuleType.OutfitDefault, "Default Outfit", HexBrush("#B48EAD"), null);
        AddGroups(dict, summary.SpellConflicts, RuleType.Spell, "Spell", HexBrush("#A3BE8C"), _formDb);
        AddGroups(dict, summary.PerkConflicts,  RuleType.Perk,  "Perk",  HexBrush("#BF616A"), _formDb);

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
        _allNpcs = new();
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
        FormNameDatabase? formDb)
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
            var winner = sorted.Count > 0 ? sorted[sorted.Count - 1] : null;

            var group = new NpcConflictGroup
            {
                RuleType   = ruleType,
                Label      = label,
                BadgeBrush = badgeBrush,
                Parent     = vm
            };

            bool isAdditive = ruleType is RuleType.Spell or RuleType.Perk;
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
                    ResolvedRuleDisplay = formDb != null
                                         ? formDb.ResolveRuleValue(src.RuleValue)
                                         : src.RuleValue,
                    SourceTool          = src.SourceTool,
                    SpidChance          = src.SpidChance,
                    SpidNpcIdentifier   = src.SpidNpcIdentifier,
                    IsAdditive          = isAdditive,
                    IsWinner            = !isAdditive &&
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

        // Stage 1: compute FilteredGroups for every NPC so the accordion only
        // shows groups that match the active type filters.
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

        // Stage 2: only show NPCs that have at least one visible group and
        // match the search text.
        var visible = _allNpcs.Where(vm =>
        {
            if (vm.FilteredGroups.Count == 0) return false;
            if (string.IsNullOrEmpty(search)) return true;
            return vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.SubText.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // Sync the persistent ObservableCollection in-place so WPF does not destroy
        // and recreate containers while accordion animations may be running.
        // visible is already sorted; _npcListSource is kept in the same order.
        var visibleSet = new HashSet<NpcConflictViewModel>(visible, ReferenceEqualityComparer.Instance);

        for (int i = _npcListSource.Count - 1; i >= 0; i--)
        {
            if (!visibleSet.Contains(_npcListSource[i]))
            {
                // Collapse before removal so the VM is in a clean state if it is
                // re-inserted later — a new container with IsExpanded=true would
                // immediately fire the expand animation before layout is complete.
                _npcListSource[i].IsExpanded = false;
                _npcListSource.RemoveAt(i);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var vm = visible[i];
            if (i >= _npcListSource.Count)
            {
                _npcListSource.Add(vm);
            }
            else if (!ReferenceEquals(_npcListSource[i], vm))
            {
                var existing = -1;
                for (int j = i + 1; j < _npcListSource.Count; j++)
                    if (ReferenceEquals(_npcListSource[j], vm)) { existing = j; break; }
                if (existing >= 0)
                    _npcListSource.Move(existing, i);
                else
                    _npcListSource.Insert(i, vm);
            }
        }

        NpcCountText.Text = $"{visible.Count} NPC{(visible.Count == 1 ? "" : "s")}";

        if (_allNpcs.Count == 0)
        {
            EmptyText.Text       = "Run analysis to populate this view.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (visible.Count == 0)
        {
            EmptyText.Text       = "No NPCs match the current filter.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void NpcHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NpcConflictViewModel vm)
            vm.IsExpanded = !vm.IsExpanded;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                if (_allNpcs.Count > 0) ApplyFilter();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void FilterA_Click(object sender, RoutedEventArgs e)
    {
        _filterA = !_filterA;
        UpdateFilterBtn(FilterAButton, _filterA, "#EBCB8B");
        ApplyFilter();
    }

    private void FilterS_Click(object sender, RoutedEventArgs e)
    {
        _filterS = !_filterS;
        UpdateFilterBtn(FilterSButton, _filterS, "#D08770");
        ApplyFilter();
    }

    private void FilterO_Click(object sender, RoutedEventArgs e)
    {
        _filterO = !_filterO;
        UpdateFilterBtn(FilterOButton, _filterO, "#B48EAD");
        ApplyFilter();
    }

    private void FilterSp_Click(object sender, RoutedEventArgs e)
    {
        _filterSp = !_filterSp;
        UpdateFilterBtn(FilterSpButton, _filterSp, "#A3BE8C");
        ApplyFilter();
    }

    private void FilterP_Click(object sender, RoutedEventArgs e)
    {
        _filterP = !_filterP;
        UpdateFilterBtn(FilterPButton, _filterP, "#BF616A");
        ApplyFilter();
    }

    private void SpidFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastSummary is null) return;
        Populate(_lastSummary);
    }

    private static void UpdateFilterBtn(Button btn, bool active, string activeHex)
    {
        btn.Background = active ? HexBrush(activeHex) : HexBrush("#4C566A");
        btn.Foreground = active ? HexBrush("#2E3440")  : HexBrush("#88C0D0");
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

        foreach (var src in toComment)
        {
            try
            {
                if (src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier))
                    ConflictResolutionHelper.RemoveNpcFromSpidLine(
                        src.FilePath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier);
                else
                    ConflictResolutionHelper.CommentOutLine(
                        src.FilePath, src.LineNumber, src.ConflictLineText);
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

        try
        {
            if (src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier))
                ConflictResolutionHelper.RemoveNpcFromSpidLine(
                    src.FilePath, src.LineNumber, src.ConflictLineText, src.SpidNpcIdentifier);
            else
                ConflictResolutionHelper.CommentOutLine(
                    src.FilePath, src.LineNumber, src.ConflictLineText);
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

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not NpcTabSourceViewModel src) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(src.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{ex.Message}", "SkyScope — Open File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public class NpcConflictViewModel : INotifyPropertyChanged
{
    public string DisplayName           { get; set; } = "";
    public string SubText               { get; set; } = "";
    public string NormalizedKey         { get; set; } = "";
    public string OverallWinnerFileName { get; set; } = "";
    public ObservableCollection<NpcConflictGroup> Groups { get; } = new();

    private bool _hasAppearance;
    public bool HasAppearance
    {
        get => _hasAppearance;
        set { _hasAppearance = value; OnPropertyChanged(); }
    }

    private bool _hasSkin;
    public bool HasSkin
    {
        get => _hasSkin;
        set { _hasSkin = value; OnPropertyChanged(); }
    }

    private bool _hasOutfit;
    public bool HasOutfit
    {
        get => _hasOutfit;
        set { _hasOutfit = value; OnPropertyChanged(); }
    }

    private bool _hasSpell;
    public bool HasSpell
    {
        get => _hasSpell;
        set { _hasSpell = value; OnPropertyChanged(); }
    }

    private bool _hasPerk;
    public bool HasPerk
    {
        get => _hasPerk;
        set { _hasPerk = value; OnPropertyChanged(); }
    }

    private List<NpcConflictGroup> _filteredGroups = new();
    public List<NpcConflictGroup> FilteredGroups
    {
        get => _filteredGroups;
        set { _filteredGroups = value; OnPropertyChanged(); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

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

public class NpcTabSourceViewModel : INotifyPropertyChanged
{
    public string  FileName          { get; init; } = "";
    public string  FilePath          { get; init; } = "";
    public int     LineNumber        { get; init; }
    public string  ConflictLineText  { get; init; } = "";
    public string? PrecedingLine     { get; init; }
    public string? FollowingLine     { get; init; }
    public int     LoadPosition      { get; init; }
    public int     TotalSources      { get; init; }
    public string  RuleValue            { get; init; } = "";
    public string  ResolvedRuleDisplay  { get; init; } = "";
    public bool    IsAdditive           { get; init; }
    public string  SourceTool           { get; init; } = "SkyPatcher";
    public int?    SpidChance        { get; init; }
    public string? SpidNpcIdentifier { get; init; }
    public NpcConflictGroup? Group   { get; set;  }

    public bool   IsSpid             => SourceTool == "SPID";
    public string SpidBadgeText      => SpidChance.HasValue ? $"SPID  {SpidChance.Value}%" : "SPID";
    public int    PrecedingLineNumber => LineNumber - 1;
    public int    FollowingLineNumber => LineNumber + 1;
    public bool   HasPrecedingLine    => !string.IsNullOrEmpty(PrecedingLine);
    public bool   HasFollowingLine    => !string.IsNullOrEmpty(FollowingLine);

    private bool _isWinner;
    public bool IsWinner
    {
        get => _isWinner;
        set { _isWinner = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
