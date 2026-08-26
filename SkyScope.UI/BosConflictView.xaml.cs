using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public partial class BosConflictView : ConflictViewBase
{
    private List<BosConflictViewModel> _allConflicts = new();
    private readonly ObservableCollection<BosConflictViewModel> _listSource = new();

    public HistoryStore? HistoryStore { get; set; }
    public EditOutputOptions OutputOptions { get; set; }

    public BosConflictView()
    {
        InitializeComponent();
        ConflictList.ItemsSource = _listSource;
    }

    public void Populate(BosConflictSummary summary)
    {
        foreach (var vm in _allConflicts)
            vm.IsExpanded = false;

        _allConflicts = summary.SwapConflicts
            .Select(entry =>
            {
                // When every source is a sub-100% chance rule, the outcome is non-deterministic —
                // BOS picks at runtime — so no source is the true "winner". Match the SPID outfit
                // handling in the NPC view and suppress the winner badge for those groups.
                var allProbabilistic = entry.Sources.Count > 0
                    && entry.Sources.All(s => s.Chance.HasValue && s.Chance.Value < 100);

                var sources = entry.Sources
                    .Select((src, idx) => new BosSourceViewModel
                    {
                        FileName           = Path.GetFileName(src.FilePath),
                        FilePath           = src.FilePath,
                        LineNumber         = src.LineNumber,
                        LineText           = src.LineText,
                        PrecedingLine      = src.PrecedingLine,
                        FollowingLine      = src.FollowingLine,
                        SwapTarget         = src.SwapTarget,
                        ConditionalSection = src.ConditionalSection,
                        Chance             = src.Chance,
                        LoadPosition       = idx + 1,
                        TotalSources       = entry.Sources.Count,
                        IsWinner           = !allProbabilistic && idx == entry.Sources.Count - 1
                    })
                    .ToList();

                var vm = new BosConflictViewModel
                {
                    DisplayName = entry.DisplayName,
                    SubText     = entry.ObjectRef.RefType == BosRefType.FormIdWithPlugin
                                  ? $"{entry.ObjectRef.Plugin} · 0x{entry.ObjectRef.FormId}"
                                  : "",
                    Sources     = new ObservableCollection<BosSourceViewModel>(sources)
                };

                foreach (var src in sources) src.Parent = vm;
                return vm;
            })
            .ToList();

        ApplyFilter();
    }

    public void Clear()
    {
        _allConflicts.Clear();
        _listSource.Clear();
        ConflictCountText.Text = "";
        EmptyText.Text         = "Run analysis to populate this view.";
        EmptyText.Visibility   = Visibility.Visible;
    }

    private void ApplyFilter()
    {
        var search  = SearchBox.Text.Trim();
        var visible = _allConflicts.Where(vm =>
        {
            if (string.IsNullOrEmpty(search)) return true;
            return vm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.SubText.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        SyncList(_listSource, visible, vm => vm.IsExpanded = false);

        ConflictCountText.Text = $"{visible.Count} conflict{(visible.Count == 1 ? "" : "s")}";

        UpdateEmptyState(EmptyText, _allConflicts.Count > 0, visible.Count,
            "Run analysis to populate this view.",
            "No conflicts match the current filter.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, () => _allConflicts.Count > 0);

    private void MakeWinner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not BosSourceViewModel winner) return;
        var vm = winner.Parent;
        if (vm == null) return;

        var toComment = vm.Sources
            .Where(s => !ReferenceEquals(s, winner))
            .ToList();

        var errors   = new List<string>();
        int modified = 0;

        var description = $"{vm.DisplayName} — BOS swap conflict";

        foreach (var src in toComment)
        {
            try
            {
                var editPath = EditOutputPathResolver.ResolveForEdit(src.FilePath, OutputOptions);
                ConflictResolutionHelper.CommentOutLine(
                    editPath, src.LineNumber, src.LineText,
                    description, "BOS", HistoryStore);
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

            foreach (var src in toComment)
                vm.Sources.Remove(src);

            if (vm.Sources.Count < 2)
            {
                _allConflicts.Remove(vm);
                ApplyFilter();
            }
        }
    }
}

public class BosConflictViewModel : INotifyPropertyChanged, IConflictItemVm
{
    public string DisplayName { get; set; } = "";
    public string SubText     { get; set; } = "";
    public int    SourceCount => Sources.Count;
    public ObservableCollection<BosSourceViewModel> Sources { get; set; } = new();

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BosSourceViewModel : IConflictSourceVm
{
    public string  FileName           { get; init; } = "";
    public string  FilePath           { get; init; } = "";
    public string  DisplayPath        => ConflictViewBase.ToSkyrimRelativePath(FilePath);
    public int     LineNumber         { get; init; }
    public string  LineText           { get; init; } = "";
    public string? PrecedingLine      { get; init; }
    public string? FollowingLine      { get; init; }
    public string  SwapTarget         { get; init; } = "";
    public string? ConditionalSection { get; init; }
    public double? Chance             { get; init; }
    public int     LoadPosition       { get; init; }
    public int     TotalSources       { get; init; }
    public bool    IsWinner           { get; init; }
    public BosConflictViewModel? Parent { get; set; }

    public bool HasConditionalSection => !string.IsNullOrEmpty(ConditionalSection);
    public bool HasChance             => Chance.HasValue;
    public string ChanceBadgeText     => Chance.HasValue
        ? $"Chance {Chance.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%"
        : "";
    public int  PrecedingLineNumber   => LineNumber - 1;
    public int  FollowingLineNumber   => LineNumber + 1;
    public bool HasPrecedingLine      => !string.IsNullOrEmpty(PrecedingLine);
    public bool HasFollowingLine      => !string.IsNullOrEmpty(FollowingLine);
}
