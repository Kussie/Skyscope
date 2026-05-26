using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public static void CommentOutLine(string filePath, int lineNumber, string capturedText)
    {
        var lines = File.ReadAllLines(filePath);
        var idx   = lineNumber - 1;

        if (idx < 0 || idx >= lines.Length)
            throw new InvalidOperationException($"Line {lineNumber} no longer exists in the file.");

        if (!string.Equals(lines[idx].Trim(), capturedText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Line {lineNumber} has changed since the last analysis — re-run analysis first.");

        if (lines[idx].TrimStart().StartsWith(";"))
            return;

        var updated = new List<string>(lines);
        updated.Insert(idx, "; Rule commented out by SkyScope");
        updated[idx + 1] = ";" + updated[idx + 1];

        File.WriteAllLines(filePath, updated);
    }

    public static void RemoveNpcFromSpidLine(string filePath, int lineNumber, string capturedText, string npcIdentifier)
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
            var fallback = new List<string>(lines);
            fallback.Insert(idx, "; Rule commented out by SkyScope");
            fallback[idx + 1] = ";" + fallback[idx + 1];
            File.WriteAllLines(filePath, fallback);
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

        var updated = new List<string>(lines);

        if (!anyRemoved)
        {
            updated.Insert(idx, "; Rule commented out by SkyScope");
            updated[idx + 1] = ";" + updated[idx + 1];
            File.WriteAllLines(filePath, updated);
            return;
        }

        bool npcsRemain = (fields.Length > 1 && HasNpcs(fields[1])) ||
                          (fields.Length > 2 && HasNpcs(fields[2]));

        if (!npcsRemain)
        {
            updated.Insert(idx, $"; {npcIdentifier} was the only target — rule commented out by SkyScope");
            updated[idx + 1] = ";" + updated[idx + 1];
            File.WriteAllLines(filePath, updated);
            return;
        }

        var indent  = line.Length - line.TrimStart().Length;
        var newLine = new string(' ', indent) + prefix + " " + string.Join("|", fields);
        updated.Insert(idx, $"{new string(' ', indent)}; {npcIdentifier} removed from rule to resolve a conflict by SkyScope");
        updated[idx + 1] = newLine;
        File.WriteAllLines(filePath, updated);
    }
}
