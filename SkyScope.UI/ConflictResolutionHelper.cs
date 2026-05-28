using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

public static class ConflictResolutionHelper
{
    // Returns a filtered copy of entries, removing SPID sources with SpidChance < 100 when
    // showLowChance is false. Entries that no longer have 2+ distinct-value sources are dropped.
    public static List<ConflictEntry> FilterLowChanceSpid(List<ConflictEntry> entries, bool showLowChance)
    {
        if (showLowChance) return entries;

        var result = new List<ConflictEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var filtered = entry.Sources
                .Where(s => !(s.SourceTool == "SPID" && s.SpidChance.HasValue && s.SpidChance.Value < 100))
                .ToList();

            if (filtered.Count < 2) continue;

            var first = filtered[0].RuleValue;
            if (filtered.All(s => string.Equals(s.RuleValue, first, StringComparison.OrdinalIgnoreCase)))
                continue;

            var copy = new ConflictEntry
            {
                NpcRef           = entry.NpcRef,
                ResolvedName     = entry.ResolvedName,
                ResolvedEditorId = entry.ResolvedEditorId
            };
            copy.Sources.AddRange(filtered);
            result.Add(copy);
        }
        return result;
    }

    public static void CommentOutLine(
        string filePath, int lineNumber, string capturedText,
        string conflictDescription = "", string sourceTool = "",
        HistoryStore? history = null)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        if (lines[idx].TrimStart().StartsWith(";"))
            return;

        var record = new ChangeRecord
        {
            Modification        = ModificationType.LineCommented,
            FilePath            = filePath,
            OriginalLineNumber  = lineNumber,
            OriginalLine        = lines[idx],
            ConflictDescription = conflictDescription,
            SourceTool          = sourceTool
        };

        var commentLine    = $"; SkyScope [{record.ChangeCode}]: Rule commented out - {conflictDescription}".TrimEnd(' ', '-');
        var commentedLine  = ";" + lines[idx];

        record.ReplacementLines = [commentLine, commentedLine];

        var updated = new List<string>(lines);
        updated.Insert(idx, commentLine);
        updated[idx + 1] = commentedLine;

        File.WriteAllLines(filePath, updated);
        history?.Add(record);
    }

    public static void RemoveNpcFromSpidLine(
        string filePath, int lineNumber, string capturedText, string npcIdentifier,
        string conflictDescription = "", string sourceTool = "",
        HistoryStore? history = null)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        if (lines[idx].TrimStart().StartsWith(";"))
            return;

        var line  = lines[idx];
        var eqIdx = line.IndexOf('=');
        if (eqIdx < 0)
        {
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, history);
            return;
        }

        var prefix   = line[..(eqIdx + 1)].TrimEnd();
        var rawValue = line[(eqIdx + 1)..].TrimStart();
        var fields   = rawValue.Split('|');

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

        if (!anyRemoved)
        {
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, history);
            return;
        }

        bool npcsRemain = (fields.Length > 1 && HasNpcs(fields[1])) ||
                          (fields.Length > 2 && HasNpcs(fields[2]));

        if (!npcsRemain)
        {
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, history);
            return;
        }

        // Rule survives with the NPC removed — Type 2 modification.
        var record = new ChangeRecord
        {
            Modification        = ModificationType.RuleModified,
            FilePath            = filePath,
            OriginalLineNumber  = lineNumber,
            OriginalLine        = line,
            ConflictDescription = conflictDescription,
            SourceTool          = sourceTool
        };

        var indent  = line.Length - line.TrimStart().Length;
        var newLine = new string(' ', indent) + prefix + " " + string.Join("|", fields);
        var commentLine = $"{new string(' ', indent)}; SkyScope [{record.ChangeCode}]: Rule modified - {conflictDescription}".TrimEnd(' ', '-');

        record.ReplacementLines = [commentLine, newLine];

        var updated = new List<string>(lines);
        updated.Insert(idx, commentLine);
        updated[idx + 1] = newLine;
        File.WriteAllLines(filePath, updated);
        history?.Add(record);
    }

    // Type 3: split one rule into multiple active rules (for future use).
    public static void SplitRule(
        string filePath, int lineNumber, string capturedText,
        List<string> newRules, string conflictDescription = "", string sourceTool = "",
        HistoryStore? history = null)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        var record = new ChangeRecord
        {
            Modification        = ModificationType.RuleSplit,
            FilePath            = filePath,
            OriginalLineNumber  = lineNumber,
            OriginalLine        = lines[idx],
            ConflictDescription = conflictDescription,
            SourceTool          = sourceTool
        };

        var code        = record.ChangeCode;
        var desc        = string.IsNullOrEmpty(conflictDescription) ? "" : $" - {conflictDescription}";
        var origComment = $"; SkyScope [{code}]: Original rule: {lines[idx]}";
        var startMarker = $"; SkyScope [{code}]: Rule modification start{desc}";
        var endMarker   = $"; SkyScope [{code}]: Rule modification end";

        List<string> replacement = [origComment, startMarker];
        replacement.AddRange(newRules);
        replacement.Add(endMarker);
        record.ReplacementLines = replacement;

        var updated = new List<string>(lines);
        updated.RemoveAt(idx);
        updated.InsertRange(idx, replacement);

        File.WriteAllLines(filePath, updated);
        history?.Add(record);
    }

    // Helper: comment out a line with Type 1 format and record to history.
    private static void WriteCommentOut(
        string filePath, string[] lines, int idx, string originalLine,
        string conflictDescription, HistoryStore? history)
    {
        var record = new ChangeRecord
        {
            Modification        = ModificationType.LineCommented,
            FilePath            = filePath,
            OriginalLineNumber  = idx + 1,
            OriginalLine        = originalLine,
            ConflictDescription = conflictDescription
        };

        var commentLine   = $"; SkyScope [{record.ChangeCode}]: Rule commented out - {conflictDescription}".TrimEnd(' ', '-');
        var commentedLine = ";" + originalLine;

        record.ReplacementLines = [commentLine, commentedLine];

        var updated = new List<string>(lines);
        updated.Insert(idx, commentLine);
        updated[idx + 1] = commentedLine;

        File.WriteAllLines(filePath, updated);
        history?.Add(record);
    }
}
