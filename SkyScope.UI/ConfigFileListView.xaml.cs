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

public enum FileHighlightState { None, Selected, LosesToSelected, BeatsSelected }

public class ConfigFileViewModel : INotifyPropertyChanged
{
    public string FullPath       { get; init; } = "";
    public string RelativePath   { get; init; } = "";
    public int    LoadOrderIndex { get; init; }
    public int    ConflictCount  { get; init; }
    public bool   HasConflicts   => ConflictCount > 0;

    private FileHighlightState _highlight = FileHighlightState.None;
    public FileHighlightState Highlight
    {
        get => _highlight;
        set { if (_highlight == value) return; _highlight = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Filterable list of config files (SkyPatcher or SPID) in load order. Selecting one highlights
// files it conflicts with — earlier (green, loses) or later (red, wins).
public partial class ConfigFileListView : ConflictViewBase
{
    private List<ConfigFileViewModel> _allFiles = new();
    private readonly ObservableCollection<ConfigFileViewModel> _listSource = new();

    // FilePath -> set of other FilePaths it shares a conflicting NPC entry with.
    private readonly Dictionary<string, HashSet<string>> _conflictsByFile = new(StringComparer.OrdinalIgnoreCase);

    private ConfigFileViewModel? _selected;
    private string _noDataMessage = "Run an analysis to populate this view.";

    public ConfigFileListView()
    {
        InitializeComponent();
        FileList.ItemsSource = _listSource;
    }

    public void Clear()
    {
        _selected = null;
        _allFiles.Clear();
        _listSource.Clear();
        _conflictsByFile.Clear();
        SearchBox.Text = "";
        _noDataMessage = "Run an analysis to populate this view.";
        EmptyText.Text       = _noDataMessage;
        EmptyText.Visibility = Visibility.Visible;
        CountText.Text       = "";
    }

    public void Populate(IReadOnlyList<string> files, ConflictSummary summary, string sourceTool,
        string noDataMessage = "No files found.")
    {
        _selected      = null;
        _noDataMessage = noDataMessage;
        _conflictsByFile.Clear();

        foreach (var entry in AllEntries(summary))
        {
            var matched = entry.Sources
                .Where(s => s.SourceTool == sourceTool)
                .Select(s => s.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < matched.Count; i++)
                for (int j = i + 1; j < matched.Count; j++)
                {
                    AddConflictPair(matched[i], matched[j]);
                    AddConflictPair(matched[j], matched[i]);
                }
        }

        _allFiles = files
            .Select((path, idx) => new ConfigFileViewModel
            {
                FullPath       = path,
                RelativePath   = BuildRelativePath(path),
                LoadOrderIndex = idx + 1,
                ConflictCount  = _conflictsByFile.TryGetValue(path, out var set) ? set.Count : 0
            })
            .ToList();

        ApplyFilter();
    }

    private void AddConflictPair(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
        if (!_conflictsByFile.TryGetValue(a, out var set))
            _conflictsByFile[a] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(b);
    }

    // Under SkyPatcher\, trim to that root; otherwise (SPID's flat Data\*_DISTR.ini) fall back
    // to the Data-relative path.
    private static string BuildRelativePath(string fullPath)
    {
        const string marker = @"\SkyPatcher\";
        var idx = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fullPath[(idx + marker.Length)..] : ToSkyrimRelativePath(fullPath);
    }

    private static IEnumerable<ConflictEntry> AllEntries(ConflictSummary s) =>
        s.AppearanceConflicts.Concat(s.SkinConflicts).Concat(s.OutfitDefaultConflicts)
         .Concat(s.SpellConflicts).Concat(s.PerkConflicts);

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim() ?? "";
        var visible = string.IsNullOrEmpty(term)
            ? _allFiles
            : _allFiles.Where(f => f.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        SyncList(_listSource, visible);

        UpdateEmptyState(EmptyText, _allFiles.Count > 0, _listSource.Count, _noDataMessage, "No files match your search.");
        CountText.Text = _allFiles.Count == 0 ? ""
            : visible.Count == _allFiles.Count
                ? (_allFiles.Count == 1 ? "1 file" : $"{_allFiles.Count:N0} files")
                : $"{visible.Count:N0} / {_allFiles.Count:N0} files";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        StartSearchDebounce(ApplyFilter, () => _allFiles.Count > 0);

    private void FileRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConfigFileViewModel vm }) return;

        if (ReferenceEquals(_selected, vm))
        {
            ClearHighlights();
            return;
        }

        SelectFile(vm);
    }

    private void SelectFile(ConfigFileViewModel vm)
    {
        _selected = vm;
        _conflictsByFile.TryGetValue(vm.FullPath, out var conflicts);

        foreach (var file in _allFiles)
        {
            if (ReferenceEquals(file, vm))
            {
                file.Highlight = FileHighlightState.Selected;
                continue;
            }

            if (conflicts == null || !conflicts.Contains(file.FullPath))
            {
                file.Highlight = FileHighlightState.None;
                continue;
            }

            // Earlier in load order → the selected file (loading later) wins over it.
            // Later in load order → that file wins over the selected one.
            file.Highlight = file.LoadOrderIndex < vm.LoadOrderIndex
                ? FileHighlightState.LosesToSelected
                : FileHighlightState.BeatsSelected;
        }
    }

    private void ClearHighlights()
    {
        _selected = null;
        foreach (var file in _allFiles)
            file.Highlight = FileHighlightState.None;
    }
}
