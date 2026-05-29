using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SkyScope.UI;

// Settings persistence and Skyrim install-path detection/normalisation.
public partial class MainWindow
{
    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
            File.WriteAllLines(settingsPath, new[]
            {
                $"SkyrimPath:{NormaliseSkyrimPath(SkyrimPathTextBox.Text ?? "")}",
                $"HistoryDiffMode:{HistoryViewControl.DiffMode}"
            });
            StatusTextBlock.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to save settings: {ex.Message}";
        }
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
