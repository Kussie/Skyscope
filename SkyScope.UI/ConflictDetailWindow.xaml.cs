using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SkyScope.Models;

namespace SkyScope.UI;

public partial class ConflictDetailWindow : Window
{
    private List<SourceViewModel> _sources = new();

    public ConflictDetailWindow(ConflictEntry entry, RuleType ruleType)
    {
        InitializeComponent();
        Populate(entry, ruleType);
    }

    private void Populate(ConflictEntry entry, RuleType ruleType)
    {
        NpcNameText.Text = entry.DisplayName;

        if (!string.IsNullOrEmpty(entry.ResolvedEditorId) || !string.IsNullOrEmpty(entry.ResolvedName))
            NpcSubText.Text = entry.NpcRef.DisplayText;
        else
            NpcSubText.Visibility = Visibility.Collapsed;

        RuleTypeText.Text = ruleType switch
        {
            RuleType.Appearance    => "Appearance  (copyVisualStyle)",
            RuleType.Skin          => "Skin",
            RuleType.OutfitDefault => "Default Outfit  (outfitDefault)",
            _                      => ruleType.ToString()
        };

        // SkyPatcher load order: alphabetical by full path, then by line number within the same file.
        // The last entry in this sorted list is the rule that actually takes effect.
        var sorted = entry.Sources
            .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.LineNumber)
            .ToList();
        var winner = sorted.Count > 0 ? sorted[sorted.Count - 1] : null;

        _sources = sorted.Select((src, idx) => new SourceViewModel
        {
            FileName     = Path.GetFileName(src.FilePath),
            FilePath     = src.FilePath,
            LineNumber   = src.LineNumber,
            RuleValue    = src.RuleValue,
            IsWinner     = winner != null &&
                           string.Equals(src.FilePath, winner.FilePath, StringComparison.OrdinalIgnoreCase) &&
                           src.LineNumber == winner.LineNumber,
            LoadPosition = idx + 1,
            TotalSources = sorted.Count,
            Lines             = BuildLineItems(src),
            SourceTool        = src.SourceTool,
            SpidChance        = src.SpidChance,
            SpidNpcIdentifier = src.SpidNpcIdentifier
        }).ToList();

        SourcesList.ItemsSource = _sources;
    }

    private static List<LineItem> BuildLineItems(ConflictSource src)
    {
        var items = new List<LineItem>();

        if (src.PrecedingLine != null)
            items.Add(new LineItem { LineNumber = src.LineNumber - 1, Text = src.PrecedingLine, IsConflict = false });

        items.Add(new LineItem { LineNumber = src.LineNumber, Text = src.ConflictLine, IsConflict = true });

        if (src.FollowingLine != null)
            items.Add(new LineItem { LineNumber = src.LineNumber + 1, Text = src.FollowingLine, IsConflict = false });

        return items;
    }

    private void MakeWinner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SourceViewModel winner) return;

        var toComment = _sources
            .Where(s => !(string.Equals(s.FilePath, winner.FilePath, StringComparison.OrdinalIgnoreCase)
                          && s.LineNumber == winner.LineNumber))
            .ToList();

        var errors   = new List<string>();
        int modified = 0;

        foreach (var src in toComment)
        {
            var conflictLine = src.Lines.FirstOrDefault(l => l.IsConflict);
            if (conflictLine == null) continue;

            try
            {
                if (src.IsSpid && !string.IsNullOrEmpty(src.SpidNpcIdentifier))
                    ConflictResolutionHelper.RemoveNpcFromSpidLine(src.FilePath, conflictLine.LineNumber, conflictLine.Text, src.SpidNpcIdentifier);
                else
                    ConflictResolutionHelper.CommentOutLine(src.FilePath, conflictLine.LineNumber, conflictLine.Text);
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
                $"Done — commented out conflicting rules in {modified} file(s).\n\nRe-run analysis to see the updated results.",
                "SkyScope — Make Winner", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SourceViewModel src) return;

        try
        {
            Process.Start(new ProcessStartInfo(src.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{ex.Message}", "SkyScope — Open File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public class SourceViewModel
{
    public string         FileName     { get; init; } = string.Empty;
    public string         FilePath     { get; init; } = string.Empty;
    public int            LineNumber   { get; init; }
    public string         RuleValue    { get; init; } = string.Empty;
    public bool           IsWinner     { get; init; }
    public int            LoadPosition { get; init; }
    public int            TotalSources { get; init; }
    public List<LineItem> Lines        { get; init; } = new();
    public string         SourceTool        { get; init; } = "SkyPatcher";
    public int?           SpidChance        { get; init; }
    public string?        SpidNpcIdentifier { get; init; }
    public bool           IsSpid            => SourceTool == "SPID";
    public string         SpidBadgeText     => SpidChance.HasValue ? $"SPID  {SpidChance.Value}%" : "SPID";
}

public class LineItem
{
    public int    LineNumber { get; init; }
    public string Text       { get; init; } = string.Empty;
    public bool   IsConflict { get; init; }
}
