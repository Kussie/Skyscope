using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Core;
using SkyScope.Models;

namespace SkyScope.UI;

// Result of a resolution edit. LinesInserted (0 or 1) lets callers re-sync other sources' captured
// line numbers without a re-analysis. RewrittenLine is set only when a shared multi-NPC rule was
// rewritten (an NPC split out but the rule survives) — its new text, so the other NPCs' sources on
// that same line can be re-pointed to it.
public readonly record struct EditResult(int LinesInserted, string? RewrittenLine);

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

    // Returns a filtered copy of entries with plugin (ESP/ESL overhaul) sources removed when
    // hidePlugins is true. Entries that no longer have 2+ distinct-value sources are dropped — so a
    // plugin-vs-plugin conflict disappears entirely and a SPID/SkyPatcher conflict that was only a
    // conflict because of a plugin also drops, while genuine config conflicts remain.
    public static List<ConflictEntry> FilterPluginSources(List<ConflictEntry> entries, bool hidePlugins)
    {
        if (!hidePlugins) return entries;

        var result = new List<ConflictEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var filtered = entry.Sources.Where(s => s.SourceTool != "Plugin").ToList();

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

    // Comments the whole rule line out. Returns how many lines were inserted (1, or 0 if it was
    // already commented) so callers can keep other sources' captured line numbers in sync and avoid
    // re-running the analysis between edits.
    public static EditResult CommentOutLine(
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
            return new EditResult(0, null);

        WriteCommentOut(filePath, lines, idx, lines[idx], conflictDescription, sourceTool, history);
        return new EditResult(1, null);
    }

    // Removes one NPC from a shared SPID rule. If other NPCs remain the line is rewritten (rule
    // survives) and its new text is returned in EditResult.RewrittenLine so the other NPCs' sources
    // on that line can be re-pointed; otherwise the whole line is commented out. LinesInserted is 1
    // when it acted, 0 for a no-op — see CommentOutLine.
    public static EditResult RemoveNpcFromSpidLine(
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
            return new EditResult(0, null);

        var line  = lines[idx];
        var eqIdx = line.IndexOf('=');
        if (eqIdx < 0)
        {
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, sourceTool, history);
            return new EditResult(1, null);
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
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, sourceTool, history);
            return new EditResult(1, null);
        }

        bool npcsRemain = (fields.Length > 1 && HasNpcs(fields[1])) ||
                          (fields.Length > 2 && HasNpcs(fields[2]));

        if (!npcsRemain)
        {
            WriteCommentOut(filePath, lines, idx, line, conflictDescription, sourceTool, history);
            return new EditResult(1, null);
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
        // The rule survives with this NPC removed — hand back its new text so the callers can
        // re-point the other NPCs that shared this line.
        return new EditResult(1, newLine.Trim());
    }

    // Core comment-out: writes the Type 1 comment block and records it to history.
    private static void WriteCommentOut(
        string filePath, string[] lines, int idx, string originalLine,
        string conflictDescription, string sourceTool, HistoryStore? history)
    {
        var record = new ChangeRecord
        {
            Modification        = ModificationType.LineCommented,
            FilePath            = filePath,
            OriginalLineNumber  = idx + 1,
            OriginalLine        = originalLine,
            ConflictDescription = conflictDescription,
            SourceTool          = sourceTool
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
