using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkyScope.Core;

namespace SkyScope.UI;

// Lets the user manually pick which npcfacefinder.com mod a plugin corresponds to, for when the
// automatic fuzzy name match misses or picks the wrong one. The mod list is fetched once (or read
// from a day-old disk cache) and filtered locally as the user types.
public partial class ModMatchPickerWindow : Window
{
    public int?    SelectedModId   { get; private set; }
    public string? SelectedModName { get; private set; }

    private readonly string _pluginName;
    private List<NpcFaceFinderMod> _allMods = [];

    public ModMatchPickerWindow(string pluginName, int? currentModId)
    {
        InitializeComponent();
        _pluginName = pluginName;
        PluginNameText.Text = $"Pick the npcfacefinder.com mod that matches: {pluginName}";
        SelectedModId = currentModId;
        _ = LoadModsAsync(currentModId);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private async System.Threading.Tasks.Task LoadModsAsync(int? currentModId)
    {
        StatusText.Text = "Loading mod list…";

        var cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apicache", "mods.json");
        var mods = await NpcFaceFinderClient.GetCachedModsAsync(cachePath, TimeSpan.FromHours(24));

        // Ranked best-first by fuzzy similarity to the plugin name, so the likely candidates show
        // up at the top even before the user types anything — the search box just narrows further.
        _allMods = ModNameMatcher.RankBySimilarity(_pluginName, mods, m => m.Name);

        StatusText.Text = _allMods.Count == 0
            ? "Could not load the mod list — check your connection and try again."
            : $"{_allMods.Count:N0} mods loaded, ranked by likely match.";

        ApplyFilter();

        if (currentModId is { } id)
        {
            var current = _allMods.FirstOrDefault(m => m.Id == id);
            if (current != null) ModListBox.SelectedItem = current;
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim() ?? "";
        ModListBox.ItemsSource = string.IsNullOrEmpty(term)
            ? _allMods
            : _allMods.Where(m => m.DisplayText.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ModListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OpenModInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NpcFaceFinderMod { ExternalUrl: { } url } }) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open page:\n\n{ex.Message}", "SkyScope — Open in Browser",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ModListBox.SelectedItem is NpcFaceFinderMod mod)
        {
            SelectedModId   = mod.Id;
            SelectedModName = mod.Name;
        }
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedModId   = null;
        SelectedModName = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
