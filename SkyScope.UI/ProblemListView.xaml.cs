using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SkyScope.Models;

namespace SkyScope.UI;

public class ProblemEntryViewModel
{
    public string FilePath   { get; init; } = "";
    public int    LineNumber { get; init; }
    public string LineText   { get; init; } = "";
    public string SourceTool { get; init; } = "";
    public ProblemSeverity Severity { get; init; }
    public string Category  { get; init; } = "";
    public string Message   { get; init; } = "";

    public string SeverityLabel => Severity == ProblemSeverity.Error ? "Error" : "Warning";
    public string LineLabel     => LineNumber > 0 ? $"Line {LineNumber}" : "";
}

public class ProblemFileViewModel : INotifyPropertyChanged, IConflictItemVm
{
    public string RelativePath { get; init; } = "";
    public List<ProblemEntryViewModel> Problems { get; init; } = [];
    public ObservableCollection<ProblemEntryViewModel> FilteredProblems { get; } = new();
    public ProblemSeverity WorstSeverity { get; init; }
    public int ProblemCount => Problems.Count;

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Groups a flat ProblemEntry list into one expandable card per file — read-only, so unlike the
// NPC/Files views there's no edit-tracking machinery needed here.
public partial class ProblemListView : ConflictViewBase
{
    private List<ProblemFileViewModel> _allFiles = new();
    private readonly ObservableCollection<ProblemFileViewModel> _listSource = new();
    private string _noDataMessage = "Run an analysis to populate this view.";

    public ProblemListView()
    {
        InitializeComponent();
        FileList.ItemsSource = _listSource;
        // Set after InitializeComponent (not as a XAML attribute) — a XAML-set IsChecked fires
        // Checked immediately during the parse, before later-declared named elements exist yet.
        ShowWarningsCheckBox.IsChecked = true;
    }

    public void Clear()
    {
        _allFiles.Clear();
        _listSource.Clear();
        SearchBox.Text = "";
        ShowWarningsCheckBox.IsChecked = true;
        _noDataMessage = "Run an analysis to populate this view.";
        EmptyText.Text       = _noDataMessage;
        EmptyText.Visibility = Visibility.Visible;
        CountText.Text       = "";
    }

    public void Populate(List<ProblemEntry> problems, string noDataMessage = "No problems found.")
    {
        _noDataMessage = noDataMessage;

        _allFiles = problems
            .GroupBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProblemFileViewModel
            {
                RelativePath  = ToSkyrimRelativePath(g.Key),
                WorstSeverity = g.Any(p => p.Severity == ProblemSeverity.Error)
                    ? ProblemSeverity.Error : ProblemSeverity.Warning,
                Problems = g.OrderBy(p => p.LineNumber)
                    .Select(p => new ProblemEntryViewModel
                    {
                        FilePath   = p.FilePath,
                        LineNumber = p.LineNumber,
                        LineText   = p.LineText,
                        SourceTool = p.SourceTool,
                        Severity   = p.Severity,
                        Category   = p.Category,
                        Message    = p.Message
                    })
                    .ToList()
            })
            .OrderByDescending(vm => vm.WorstSeverity)
            .ThenBy(vm => vm.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term         = SearchBox.Text?.Trim() ?? "";
        var showWarnings = ShowWarningsCheckBox.IsChecked == true;

        var visibleFiles = new List<ProblemFileViewModel>();
        foreach (var file in _allFiles)
        {
            var matching = file.Problems.Where(p =>
                    (showWarnings || p.Severity == ProblemSeverity.Error) &&
                    (string.IsNullOrEmpty(term)
                     || file.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)
                     || p.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
                     || p.Category.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matching.Count == 0) continue;

            SyncList(file.FilteredProblems, matching);
            visibleFiles.Add(file);
        }

        SyncList(_listSource, visibleFiles);

        var noMatchMessage = "No problems match your search.";
        UpdateEmptyState(EmptyText, _allFiles.Count > 0, _listSource.Count, _noDataMessage, noMatchMessage);

        var totalProblems   = _allFiles.Sum(f => f.Problems.Count);
        var visibleProblems = visibleFiles.Sum(f => f.FilteredProblems.Count);
        CountText.Text = _allFiles.Count == 0 ? ""
            : visibleProblems == totalProblems
                ? (totalProblems == 1 ? "1 problem" : $"{totalProblems:N0} problems")
                : $"{visibleProblems:N0} / {totalProblems:N0} problems";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, () => _allFiles.Count > 0);

    private void ShowWarningsCheckBox_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OpenFileForRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProblemEntryViewModel vm }) return;
        OpenFile(vm.FilePath);
    }
}
