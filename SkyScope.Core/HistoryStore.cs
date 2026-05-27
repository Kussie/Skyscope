using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyScope.Models;

namespace SkyScope.Core;

public record RevertResult(bool Success, string? Error);

public class HistoryStore
{
    private static readonly string StorageDir  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history");
    private static readonly string StoragePath = Path.Combine(StorageDir, "history.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented       = true,
        Converters          = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private List<ChangeRecord> _records = new();

    public IReadOnlyList<ChangeRecord> Records => _records.AsReadOnly();

    public event Action<ChangeRecord>? RecordAdded;

    public void Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return;
            var json = File.ReadAllText(StoragePath);
            _records = JsonSerializer.Deserialize<List<ChangeRecord>>(json, SerializerOptions)
                       ?? new List<ChangeRecord>();
        }
        catch
        {
            _records = new List<ChangeRecord>();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(_records, SerializerOptions));
        }
        catch { /* best-effort */ }
    }

    public void Add(ChangeRecord record)
    {
        _records.Add(record);
        Save();
        RecordAdded?.Invoke(record);
    }

    public RevertResult TryRevert(string id)
    {
        var record = _records.FirstOrDefault(r => r.Id == id);
        if (record == null)
            return new RevertResult(false, "History record not found.");
        if (record.Status == ChangeStatus.Reverted)
            return new RevertResult(false, "This change has already been reverted.");
        if (record.Status == ChangeStatus.NotFound)
            return new RevertResult(false, $"Change marker [{record.ChangeCode}] was previously not found in the file.");

        if (!File.Exists(record.FilePath))
        {
            record.Status = ChangeStatus.NotFound;
            Save();
            return new RevertResult(false, $"File not found: {record.FilePath}");
        }

        var lines = File.ReadAllLines(record.FilePath).ToList();

        // Scan entire file for the change code marker.
        var markerPrefix = $"; SkyScope [{record.ChangeCode}]:";
        int blockStart = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith(markerPrefix, StringComparison.Ordinal))
            {
                blockStart = i;
                break;
            }
        }

        if (blockStart == -1)
        {
            record.Status = ChangeStatus.NotFound;
            Save();
            return new RevertResult(false,
                $"Change marker [{record.ChangeCode}] was not found in {Path.GetFileName(record.FilePath)}. " +
                "The record has been marked as not found and cannot be reverted.");
        }

        // Collect the full replacement block from the file.
        // The block = all lines that contain our marker code PLUS any immediately following
        // non-SkyScope lines up to the count recorded in ReplacementLines.
        var locatedBlock = new List<string>();
        int idx = blockStart;
        while (locatedBlock.Count < record.ReplacementLines.Count && idx < lines.Count)
            locatedBlock.Add(lines[idx++]);

        // Content validation — compare located block against stored ReplacementLines verbatim.
        if (locatedBlock.Count != record.ReplacementLines.Count ||
            !locatedBlock.SequenceEqual(record.ReplacementLines, StringComparer.Ordinal))
        {
            return new RevertResult(false,
                $"The rule in {Path.GetFileName(record.FilePath)} has been modified since SkyScope changed it. " +
                "Revert aborted. Restore the original SkyScope comment block to enable revert.");
        }

        // Perform the revert: remove replacement block, insert original line.
        lines.RemoveRange(blockStart, locatedBlock.Count);
        lines.Insert(blockStart, record.OriginalLine);

        File.WriteAllLines(record.FilePath, lines);

        record.Status = ChangeStatus.Reverted;
        Save();
        return new RevertResult(true, null);
    }
}
