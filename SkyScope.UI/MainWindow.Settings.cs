using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using Microsoft.Win32;
using SkyScope.Models;

namespace SkyScope.UI;

// Settings persistence and Skyrim install-path detection/normalisation.
public partial class MainWindow
{
    private static readonly JsonSerializerOptions AppSettingsJson = new() { WriteIndented = true };
    private static string AppSettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private AppSettings _appSettings = new();
    private readonly ObservableCollection<PluginThumbnailRow> _pluginThumbnailRows = new();
    private readonly ObservableCollection<string> _ignoredPluginRows = new();

    // Reads settings.json and builds the plugin-thumbnail rows. Called on startup, after
    // EnsureAppFilesExist has guaranteed the file exists.
    private void LoadAppSettings()
    {
        try
        {
            var json = File.Exists(AppSettingsPath) ? File.ReadAllText(AppSettingsPath) : "";
            if (!string.IsNullOrWhiteSpace(json))
                _appSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            _appSettings = new AppSettings();
        }

        _pluginThumbnailRows.Clear();
        foreach (var kvp in _appSettings.PluginThumbnailDirectories
                     .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            _pluginThumbnailRows.Add(new PluginThumbnailRow
            {
                PluginName = kvp.Key,
                Directory  = kvp.Value,
                IsSaved    = !string.IsNullOrWhiteSpace(kvp.Value)
            });

        PluginThumbnailList.ItemsSource = _pluginThumbnailRows;

        _ignoredPluginRows.Clear();
        foreach (var plugin in _appSettings.IgnoredAppearancePlugins
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            _ignoredPluginRows.Add(plugin);
        IgnoredPluginList.ItemsSource = _ignoredPluginRows;

        UpdateSettingsEmptyState();
    }

    // Adds the plugin typed into the Ignored Plugins box. Persists immediately; the change takes
    // effect on the next analysis (the ignore list is applied while scanning plugins).
    private void AddIgnoredPlugin_Click(object sender, RoutedEventArgs e)
    {
        var name = IgnoredPluginInput.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (_ignoredPluginRows.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
        {
            SettingsStatusText.Text = $"{name} is already in the ignored list.";
            IgnoredPluginInput.Clear();
            return;
        }

        _ignoredPluginRows.Add(name);
        ResortIgnoredPlugins();
        IgnoredPluginInput.Clear();
        PersistIgnoredPlugins($"Added {name} to ignored plugins. Re-run analysis to apply.");
    }

    private void RemoveIgnoredPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string name }) return;

        var match = _ignoredPluginRows.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;

        _ignoredPluginRows.Remove(match);
        PersistIgnoredPlugins($"Removed {name} from ignored plugins. Re-run analysis to apply.");
    }

    private void ResortIgnoredPlugins()
    {
        var sorted = _ignoredPluginRows.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        _ignoredPluginRows.Clear();
        foreach (var p in sorted) _ignoredPluginRows.Add(p);
    }

    private void PersistIgnoredPlugins(string statusMessage)
    {
        _appSettings.IgnoredAppearancePlugins = _ignoredPluginRows.ToList();
        try
        {
            File.WriteAllText(AppSettingsPath, JsonSerializer.Serialize(_appSettings, AppSettingsJson));
            SettingsStatusText.Text = statusMessage;
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Failed to update settings: {ex.Message}";
        }
    }

    // Adds any newly-discovered appearance plugins as rows (with an empty folder), keeping the
    // list sorted and preserving directories already entered for existing plugins.
    private void MergeAppearancePlugins(IReadOnlyList<string> plugins)
    {
        var existing = new HashSet<string>(
            _pluginThumbnailRows.Select(r => r.PluginName), StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var plugin in plugins)
        {
            if (string.IsNullOrEmpty(plugin) || !existing.Add(plugin)) continue;
            _pluginThumbnailRows.Add(new PluginThumbnailRow { PluginName = plugin, Directory = "" });
            added = true;
        }

        if (added)
        {
            var sorted = _pluginThumbnailRows
                .OrderBy(r => r.PluginName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _pluginThumbnailRows.Clear();
            foreach (var row in sorted) _pluginThumbnailRows.Add(row);
        }

        UpdateSettingsEmptyState();
    }

    private void UpdateSettingsEmptyState() =>
        SettingsEmptyText.Visibility = _pluginThumbnailRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // Clicking a row in a scrolled list otherwise triggers WPF's RequestBringIntoView, which scrolls
    // the page/list to "fully reveal" the row — yanking the panels so the element shifts out from
    // under the cursor and the click never lands. Suppress it; these lists are navigated with the
    // wheel and scrollbars, so no auto-scroll-into-view is needed.
    private void SettingsRow_RequestBringIntoView(object sender, System.Windows.RequestBringIntoViewEventArgs e)
        => e.Handled = true;

    // The two settings lists have their own ScrollViewers. Once an inner list reaches its top or
    // bottom, forward further wheel scrolling to the page-level ScrollViewer so the whole Settings
    // tab keeps scrolling smoothly instead of the wheel getting trapped in the inner list.
    private void InnerScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer inner) return;

        var atTop    = inner.VerticalOffset <= 0;
        var atBottom = inner.VerticalOffset >= inner.ScrollableHeight;

        if (inner.ScrollableHeight <= 0 || (e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset - e.Delta);
        }
    }

    private void BrowseThumbnailDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: PluginThumbnailRow row }) return;

        using var dialog = new FolderBrowserDialog
        {
            Description            = $"Select the thumbnail folder for {row.PluginName}",
            UseDescriptionForTitle = true,
            ShowNewFolderButton    = true
        };

        if (!string.IsNullOrEmpty(row.Directory) && Directory.Exists(row.Directory))
            dialog.InitialDirectory = row.Directory;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            row.Directory = dialog.SelectedPath;
    }

    // Removes the saved folder for a plugin from settings.json. The row stays (the plugin may still
    // be a live appearance conflict) but its folder is cleared and the delete button hides.
    private void DeleteThumbnailDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: PluginThumbnailRow row }) return;

        _appSettings.PluginThumbnailDirectories.Remove(row.PluginName);
        row.Directory = "";
        row.IsSaved   = false;

        try
        {
            File.WriteAllText(AppSettingsPath, JsonSerializer.Serialize(_appSettings, AppSettingsJson));
            SettingsStatusText.Text = $"Removed thumbnail folder for {row.PluginName}.";

            NpcConflictViewControl.ThumbnailDirectories = _appSettings.PluginThumbnailDirectories;
            NpcConflictViewControl.RefreshPortraits();
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Failed to update settings: {ex.Message}";
        }
    }

    private void SaveAppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _appSettings.PluginThumbnailDirectories = _pluginThumbnailRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Directory))
                .ToDictionary(r => r.PluginName, r => r.Directory.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var row in _pluginThumbnailRows)
                row.IsSaved = !string.IsNullOrWhiteSpace(row.Directory);

            File.WriteAllText(AppSettingsPath, JsonSerializer.Serialize(_appSettings, AppSettingsJson));
            SettingsStatusText.Text = $"Saved {_appSettings.PluginThumbnailDirectories.Count} thumbnail folder(s) to settings.json.";

            // Re-resolve portraits in the NPC view so newly-pointed folders take effect immediately.
            NpcConflictViewControl.ThumbnailDirectories = _appSettings.PluginThumbnailDirectories;
            NpcConflictViewControl.RefreshPortraits();

            ShowToast("Settings Saved");
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Failed to save settings: {ex.Message}";
        }
    }

    // Ensures the per-install data file and thumbnails folder exist next to the executable.
    // Created on first launch; no-ops on subsequent launches.
    private static void EnsureAppFilesExist()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var settingsPath = Path.Combine(baseDir, "settings.json");
            if (!File.Exists(settingsPath))
                File.WriteAllText(settingsPath, "{}");

            var thumbnailsDir = Path.Combine(baseDir, "thumbnails");
            if (!Directory.Exists(thumbnailsDir))
                Directory.CreateDirectory(thumbnailsDir);
        }
        catch { /* best-effort */ }
    }

    private void LoadSettings()
    {
        // Auto-detect from registry first, then let saved settings override
        var detected = TryDetectSkyrimPath();
        if (!string.IsNullOrEmpty(detected))
            SkyrimPathTextBox.Text = detected;

        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
            if (!File.Exists(settingsPath)) return;
            foreach (var line in File.ReadAllLines(settingsPath))
            {
                if (line.StartsWith("SkyrimPath:"))
                {
                    var saved = NormaliseSkyrimPath(line["SkyrimPath:".Length..]);
                    if (!string.IsNullOrEmpty(saved))
                        SkyrimPathTextBox.Text = saved;
                }
                else if (line.StartsWith("HistoryDiffMode:"))
                {
                    var mode = line["HistoryDiffMode:".Length..];
                    if (Enum.TryParse<DiffMode>(mode, out var diffMode))
                        HistoryViewControl.DiffMode = diffMode;
                }
            }
        }
        catch { }
    }

    private static string? TryDetectSkyrimPath()
    {
        try
        {
            // When launched through MO2 (including Stock Game setups), MO2 sets the working
            // directory to the managed game folder. Check that first so Stock Game users don't
            // need to configure the path manually.
            var cwd = Directory.GetCurrentDirectory();
            if (IsSkyrimDirectory(cwd))
                return cwd;

            // (subkey under HKLM, value name)
            var candidates = new[]
            {
                // Steam App 489830 = Skyrim Special Edition / Anniversary Edition
                (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 489830", "InstallLocation"),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 489830",             "InstallLocation"),
                // Bethesda launcher install key
                (@"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "Installed Path"),
                (@"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",             "Installed Path"),
                // Steam App 72850 = Skyrim Legendary Edition (LE) fallback
                (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 72850", "InstallLocation"),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 72850",             "InstallLocation"),
            };

            foreach (var (subKey, valueName) in candidates)
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                           .OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string path && Directory.Exists(path))
                    return path;
            }
        }
        catch { }

        return null;
    }

    // Strips a trailing \Data or /Data segment so users can paste either the game root
    // or the Data subfolder and get the same result.
    private static string NormaliseSkyrimPath(string path)
    {
        var p = path.TrimEnd('\\', '/');
        if (p.EndsWith("\\Data", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith("/Data",  StringComparison.OrdinalIgnoreCase))
            p = p[..^5];
        return p;
    }

    private static bool IsSkyrimDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "SkyrimSE.exe"))
            || File.Exists(Path.Combine(path, "TESV.exe"))
            || File.Exists(Path.Combine(path, "SkyrimVR.exe"));
    }
}

// One row in the Settings tab: a discovered appearance plugin and the folder of its thumbnails.
public class PluginThumbnailRow : INotifyPropertyChanged
{
    public string PluginName { get; init; } = "";

    private string _directory = "";
    public string Directory
    {
        get => _directory;
        set { _directory = value; OnPropertyChanged(); }
    }

    // True when this plugin's folder is persisted in settings.json — drives the delete button.
    private bool _isSaved;
    public bool IsSaved
    {
        get => _isSaved;
        set { _isSaved = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
