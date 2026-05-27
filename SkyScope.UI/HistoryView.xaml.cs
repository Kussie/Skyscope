using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public enum DiffMode { InlineDiff, BeforeAfter }

public enum DiffLineKind { Removed, Added }

public class DiffLineViewModel
{
    public DiffLineKind Kind   { get; init; }
    public string       Text   { get; init; } = "";
    public string       Gutter => Kind == DiffLineKind.Removed ? "–" : "+";
}

public class HistoryItemViewModel : INotifyPropertyChanged, IConflictItemVm
{
    public string   Id                  { get; }
    public string   ChangeCode          { get; }
    public string   Timestamp           { get; }
    public string   FileName            { get; }
    public string   FilePath            { get; }
    public string   ConflictDescription { get; }
    public string   SourceTool          { get; }
    public string   ModificationLabel   { get; }
    public string   OriginalLine        { get; }
    public List<string>          ReplacementLines { get; }
    public List<DiffLineViewModel> DiffLines      { get; }

    private ChangeStatus _status;
    public ChangeStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRevert)); OnPropertyChanged(nameof(StatusLabel)); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool   CanRevert   => Status == ChangeStatus.Applied;
    public string StatusLabel => Status switch
    {
        ChangeStatus.Applied  => "Applied",
        ChangeStatus.Reverted => "Reverted",
        ChangeStatus.NotFound => "Not Found",
        _                     => "Unknown"
    };

    public HistoryItemViewModel(ChangeRecord record)
    {
        Id                  = record.Id;
        ChangeCode          = record.ChangeCode;
        Timestamp           = record.Timestamp.ToString("yyyy-MM-dd HH:mm");
        FileName            = System.IO.Path.GetFileName(record.FilePath);
        FilePath            = record.FilePath;
        ConflictDescription = record.ConflictDescription;
        SourceTool          = record.SourceTool;
        OriginalLine        = record.OriginalLine;
        ReplacementLines    = record.ReplacementLines;
        _status             = record.Status;

        ModificationLabel = record.Modification switch
        {
            ModificationType.LineCommented => "Line commented out",
            ModificationType.RuleModified  => "Rule modified",
            ModificationType.RuleSplit     => "Rule split",
            _                              => "Changed"
        };

        // Build diff line list for inline diff mode.
        DiffLines = new List<DiffLineViewModel>
        {
            new() { Kind = DiffLineKind.Removed, Text = record.OriginalLine }
        };
        foreach (var line in record.ReplacementLines)
            DiffLines.Add(new DiffLineViewModel { Kind = DiffLineKind.Added, Text = line });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class HistoryView : ConflictViewBase, INotifyPropertyChanged
{
    private HistoryStore?                        _store;
    private List<HistoryItemViewModel>           _allItems   = new();
    private ObservableCollection<HistoryItemViewModel> _listSource = new();
    private DispatcherTimer?                     _searchTimer;

    private DiffMode _diffMode = DiffMode.InlineDiff;
    public DiffMode DiffMode
    {
        get => _diffMode;
        set
        {
            _diffMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInlineDiff));
            OnPropertyChanged(nameof(IsBeforeAfter));
        }
    }

    public bool IsInlineDiff  => DiffMode == DiffMode.InlineDiff;
    public bool IsBeforeAfter => DiffMode == DiffMode.BeforeAfter;

    public HistoryView()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _listSource;
        DataContext = this;
    }

    public void Refresh(HistoryStore store)
    {
        _store    = store;
        _allItems = store.Records
            .OrderByDescending(r => r.Timestamp)
            .Select(r => new HistoryItemViewModel(r))
            .ToList();
        store.RecordAdded -= OnRecordAdded;
        store.RecordAdded += OnRecordAdded;
        ApplyFilter();
    }

    public void AddRecord(ChangeRecord record)
    {
        var vm = new HistoryItemViewModel(record);
        _allItems.Insert(0, vm);
        ApplyFilter();
    }

    private void OnRecordAdded(ChangeRecord record) =>
        Dispatcher.Invoke(() => AddRecord(record));

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim() ?? "";
        var visible = string.IsNullOrEmpty(term)
            ? _allItems
            : _allItems.Where(vm =>
                vm.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)         ||
                vm.ConflictDescription.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                vm.SourceTool.Contains(term, StringComparison.OrdinalIgnoreCase))
              .ToList();

        SyncList(_listSource, visible, vm => vm.IsExpanded = false);

        EmptyText.Visibility       = _listSource.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordCountText.Text       = $"{_listSource.Count} change(s)";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer?.Stop();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (_allItems.Count > 0) ApplyFilter();
        };
        _searchTimer.Start();
    }

    private void InlineDiffButton_Click(object sender, RoutedEventArgs e) =>
        DiffMode = DiffMode.InlineDiff;

    private void BeforeAfterButton_Click(object sender, RoutedEventArgs e) =>
        DiffMode = DiffMode.BeforeAfter;

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not HistoryItemViewModel vm) return;
        if (_store == null) return;

        var result = _store.TryRevert(vm.Id);

        // Sync the ViewModel status regardless of outcome (NotFound changes status too).
        var updated = _store.Records.FirstOrDefault(r => r.Id == vm.Id);
        if (updated != null) vm.Status = updated.Status;

        if (result.Success)
        {
            MessageBox.Show(
                $"Reverted: {vm.ConflictDescription}\n\n{vm.FileName}",
                "SkyScope — Revert", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(result.Error, "SkyScope — Revert Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
