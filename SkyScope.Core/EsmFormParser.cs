using System.Collections.Generic;

namespace SkyScope.Core;

// Reads arbitrary top-level record groups from a Skyrim plugin file,
// extracting EDID and FULL subrecords.  Used for SPEL/PERK lookups.
internal class EsmFormParser
{
    internal class FormEntry
    {
        public string  OriginalPlugin { get; set; } = string.Empty;
        public uint    LocalFormId    { get; set; }
        public string? EditorId       { get; set; }
        public string? FullName       { get; set; }
    }

    internal class ParseResult
    {
        public List<string>    Masters     { get; } = [];
        public bool            IsLocalised { get; set; }
        public List<FormEntry> Entries     { get; } = [];
    }

    // groupTypes: set of 4-char group labels to capture, e.g. {"SPEL", "PERK"}
    public ParseResult Parse(string filePath, HashSet<string> groupTypes)
    {
        var result = new ParseResult();

        var (_, masters, isLocalised) = EsmBinaryUtils.ForEachRecord(filePath, groupTypes,
            (origPlugin, localFormId, recData, _, loc) =>
            {
                var (editorId, fullName) = EsmBinaryUtils.ParseEdidFull(recData, loc);
                if (!string.IsNullOrEmpty(editorId) || !string.IsNullOrEmpty(fullName))
                    result.Entries.Add(new FormEntry
                    {
                        OriginalPlugin = origPlugin,
                        LocalFormId    = localFormId,
                        EditorId       = editorId,
                        FullName       = fullName
                    });
            });

        result.IsLocalised = isLocalised;
        result.Masters.AddRange(masters);
        return result;
    }
}
