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
using SkyScope.Models;

namespace SkyScope.UI;

public partial class NpcConflictView : UserControl
{
    private List<NpcConflictViewModel> _allNpcs = new();
    private bool _filterA = true;
    private bool _filterS = true;
    private bool _filterO = true;

    public NpcConflictView()
    {
        InitializeComponent();
    }

    public void Populate(ConflictSummary summary)
    {
        foreach (var vm in _allNpcs)
            vm.IsExpanded = false;

        var dict = new Dictionary<string, NpcConflictViewModel>(StringComparer.OrdinalIgnoreCase);

        AddGroups(dict, summary.AppearanceConflicts,    RuleType.Appearance,    "Appearance",     HexBrush("#EBCB8B"));
        AddGroups(dict, summary.SkinConflicts,          RuleType.Skin,          "Skin",           HexBrush("#D08770"));
        AddGroups(dict, summary.OutfitDefaultConflicts, RuleType.OutfitDefault, "Default Outfit", HexBrush("#B48EAD"));

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
        NpcList.ItemsSource  = null;
        NpcCountText.Text    = "";
        EmptyText.Text       = "Run analysis to populate this view.";
        EmptyText.Visibility = Visibility.Visible;
    }

    private static void AddGroups(
        Dictionary<string, NpcConflictViewModel> dict,
        List<ConflictEntry> entries,
        RuleType ruleType,
        string label,
        SolidColorBrush badgeBrush)
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

            group.Sources = sorted.Select((src, idx) => new NpcTabSourceViewModel
            {
                FileName          = Path.GetFileName(src.FilePath),
                FilePath          = src.FilePath,
                LineNumber        = src.LineNumber,
                ConflictLineText  = src.ConflictLine,
                PrecedingLine     = src.PrecedingLine,
                FollowingLine     = src.FollowingLine,
                LoadPosition      = idx + 1,
                TotalSources      = sorted.Count,
                RuleValue         = src.RuleValue,
                SourceTool        = src.SourceTool,
                SpidChance        = src.SpidChance,
                SpidNpcIdentifier = src.SpidNpcIdentifier,
                IsWinner          = winner != null &&
                                    string.Equals(src.FilePath, winner.FilePath, StringComparison.OrdinalIgnoreCase) &&
                                    src.LineNumber == winner.LineNumber,
                Group             = group
            }).ToList();

            vm.Groups.Add(group);

            if      (ruleType == RuleType.Appearance)    vm.HasAppearance = true;
            else if (ruleType == RuleType.Skin)          vm.HasSkin       = true;
            else                                         vm.HasOutfit     = true;

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

        var visible = _allNpcs.Where(vm =>
        {
            var typeMatch = (vm.HasAppearance && _filterA) ||
                            (vm.HasSkin       && _filterS) ||
                            (vm.HasOutfit     && _filterO);
            if (!typeMatch) return false;

            if (string.IsNullOrEmpty(search)) return true;

            return vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.SubText.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        NpcList.ItemsSource  = visible.Count > 0 ? visible : (System.Collections.IEnumerable?)null;
        NpcCountText.Text    = $"{visible.Count} NPC{(visible.Count == 1 ? "" : "s")}";

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
        if (_allNpcs.Count > 0) ApplyFilter();
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
    public RuleType                    RuleType   { get; init; }
    public string                      Label      { get; init; } = "";
    public SolidColorBrush             BadgeBrush { get; init; } = Brushes.Gray;
    public List<NpcTabSourceViewModel> Sources    { get; set;  } = new();
    public NpcConflictViewModel?       Parent     { get; set;  }
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
    public string  RuleValue         { get; init; } = "";
    public string  SourceTool        { get; init; } = "SkyPatcher";
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
