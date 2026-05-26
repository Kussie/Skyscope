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
                    RemoveNpcFromSpidLine(src.FilePath, conflictLine.LineNumber, conflictLine.Text, src.SpidNpcIdentifier);
                else
                    CommentOutLine(src.FilePath, conflictLine.LineNumber, conflictLine.Text);
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

    private static void CommentOutLine(string filePath, int lineNumber, string capturedText)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        if (lines[idx].TrimStart().StartsWith(";"))
            return; // already commented out — nothing to do

        var updated = new List<string>(lines);
        updated.Insert(idx, "; Rule commented out by SkyScope");
        updated[idx + 1] = ";" + updated[idx + 1];

        File.WriteAllLines(filePath, updated);
    }

    private static void RemoveNpcFromSpidLine(string filePath, int lineNumber, string capturedText, string npcIdentifier)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        if (lines[idx].TrimStart().StartsWith(";"))
            return; // already commented out

        var line  = lines[idx];
        var eqIdx = line.IndexOf('=');
        if (eqIdx < 0)
        {
            // Not a valid SPID line — fall back to commenting out
            var fallback = new List<string>(lines);
            fallback.Insert(idx, "; Rule commented out by SkyScope");
            fallback[idx + 1] = ";" + fallback[idx + 1];
            File.WriteAllLines(filePath, fallback);
            return;
        }

        var prefix    = line[..(eqIdx + 1)].TrimEnd(); // e.g. "Outfit ="
        var rawValue  = line[(eqIdx + 1)..].TrimStart();
        var fields    = rawValue.Split('|');

        static string FilterField(string field, string id, out bool removed)
        {
            removed = false;
            var parts = field.Split(',');
            var kept  = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                if (string.Equals(p.Trim(), id, StringComparison.OrdinalIgnoreCase))
                { removed = true; continue; }
                kept.Add(p);
            }
            return string.Join(",", kept);
        }

        static bool HasNpcs(string field) =>
            field.Split(',').Any(p => { var t = p.Trim(); return t.Length > 0 && t[0] != '-'; });

        bool anyRemoved = false;

        if (fields.Length > 1)
        {
            fields[1] = FilterField(fields[1], npcIdentifier, out var r);
            anyRemoved |= r;
        }
        if (fields.Length > 2)
        {
            fields[2] = FilterField(fields[2], npcIdentifier, out var r);
            anyRemoved |= r;
        }

        var updated = new List<string>(lines);

        if (!anyRemoved)
        {
            // Identifier not found in filter fields — fall back to commenting out the whole rule
            updated.Insert(idx, "; Rule commented out by SkyScope");
            updated[idx + 1] = ";" + updated[idx + 1];
            File.WriteAllLines(filePath, updated);
            return;
        }

        bool npcsRemain = (fields.Length > 1 && HasNpcs(fields[1])) ||
                          (fields.Length > 2 && HasNpcs(fields[2]));

        if (!npcsRemain)
        {
            // Removing this NPC would leave no targets — comment out the whole rule instead
            updated.Insert(idx, $"; {npcIdentifier} was the only target — rule commented out by SkyScope");
            updated[idx + 1] = ";" + updated[idx + 1];
            File.WriteAllLines(filePath, updated);
            return;
        }

        // Reconstruct with the NPC removed and a comment above
        var indent     = line.Length - line.TrimStart().Length;
        var newLine    = new string(' ', indent) + prefix + " " + string.Join("|", fields);
        updated.Insert(idx, $"{new string(' ', indent)}; {npcIdentifier} removed from rule to resolve a conflict by SkyScope");
        updated[idx + 1] = newLine;
        File.WriteAllLines(filePath, updated);
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
