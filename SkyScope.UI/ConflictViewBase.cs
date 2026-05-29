using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SkyScope.UI;

// Implemented by NpcConflictViewModel and BosConflictViewModel.
public interface IConflictItemVm
{
    bool IsExpanded { get; set; }
}

// Implemented by NpcTabSourceViewModel and BosSourceViewModel.
public interface IConflictSourceVm
{
    string FilePath { get; }
}

public abstract class ConflictViewBase : UserControl
{
    private DispatcherTimer? _searchDebounce;

    // Call from SearchBox.TextChanged. Fires filterAction after 200 ms of inactivity,
    // but only when hasItems returns true.
    protected void StartSearchDebounce(Action filterAction, Func<bool> hasItems)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                if (hasItems()) filterAction();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // Syncs destination to match source in-place, preserving existing container identities
    // so WPF does not destroy containers while accordion animations may be running.
    protected static void SyncList<T>(
        ObservableCollection<T> destination,
        List<T> source,
        Action<T>? onRemove = null) where T : class
    {
        var keep = new HashSet<T>(source, ReferenceEqualityComparer.Instance);

        for (int i = destination.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(destination[i]))
            {
                onRemove?.Invoke(destination[i]);
                destination.RemoveAt(i);
            }
        }

        for (int i = 0; i < source.Count; i++)
        {
            var item = source[i];
            if (i >= destination.Count)
            {
                destination.Add(item);
            }
            else if (!ReferenceEquals(destination[i], item))
            {
                var existing = -1;
                for (int j = i + 1; j < destination.Count; j++)
                    if (ReferenceEquals(destination[j], item)) { existing = j; break; }
                if (existing >= 0)
                    destination.Move(existing, i);
                else
                    destination.Insert(i, item);
            }
        }
    }

    // Shared accordion toggle — wired via Click="ConflictHeader_Click" in both views.
    protected void ConflictHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IConflictItemVm vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    // Updates the empty-state label after a filter pass.
    // noDataMessage:  shown when the backing list has never been populated.
    // noMatchMessage: shown when items exist but none pass the current filter.
    protected static void UpdateEmptyState(
        TextBlock emptyText, bool hasData, int visibleCount,
        string noDataMessage, string noMatchMessage)
    {
        if (!hasData)
        {
            emptyText.Text       = noDataMessage;
            emptyText.Visibility = Visibility.Visible;
        }
        else if (visibleCount == 0)
        {
            emptyText.Text       = noMatchMessage;
            emptyText.Visibility = Visibility.Visible;
        }
        else
        {
            emptyText.Visibility = Visibility.Collapsed;
        }
    }

    // Shared open-file action — wired via Click="OpenFile_Click" in both views.
    protected void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IConflictSourceVm src })
            OpenFile(src.FilePath);
    }

    // Trims a full plugin/config path to one relative to the Skyrim root — i.e. starting at the
    // "Data" folder (e.g. "H:\…\Skyrim Special Edition\Data\SKSE\…" → "Data\SKSE\…").
    // Falls back to the full path when no Data segment is present.
    public static string ToSkyrimRelativePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        var idx = fullPath.IndexOf(@"\Data\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = fullPath.IndexOf("/Data/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fullPath[(idx + 1)..] : fullPath;
    }

    protected void OpenFile(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{ex.Message}", "SkyScope — Open File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
