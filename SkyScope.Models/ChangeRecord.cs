using System;
using System.Collections.Generic;

namespace SkyScope.Models;

public enum ModificationType { LineCommented, RuleModified, RuleSplit }
public enum ChangeStatus     { Applied, Reverted, NotFound }

public class ChangeRecord
{
    // Full GUID stored in JSON; first 8 chars are the change code embedded in INI comments.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // The short code written into the INI file as "; SkyScope [a3f92b1c]: ..."
    public string ChangeCode => Id[..8];

    public DateTime         Timestamp    { get; set; } = DateTime.Now;
    public ModificationType Modification { get; set; }
    public ChangeStatus     Status       { get; set; } = ChangeStatus.Applied;

    // Source location — informational hint; ChangeCode is the authoritative locator for revert.
    public string FilePath           { get; set; } = "";
    public int    OriginalLineNumber { get; set; }

    // Exact text before and after — used for content validation during revert.
    public string       OriginalLine     { get; set; } = "";
    public List<string> ReplacementLines { get; set; } = [];

    // Display context shown in the History tab.
    public string ConflictDescription { get; set; } = "";
    public string SourceTool          { get; set; } = "";
}
