using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class BosConflictDetector
{
    public BosConflictSummary DetectConflicts(List<BosSwapRule> rules, ModReferenceLibrary? library = null)
    {
        var map = new Dictionary<string, (BosObjectRef Ref, List<BosConflictSource> Sources)>();

        foreach (var rule in rules)
        {
            foreach (var orig in rule.OriginalObjects)
            {
                var key = orig.NormalizedKey;
                if (string.IsNullOrEmpty(key)) continue;

                if (!map.TryGetValue(key, out var entry))
                {
                    entry = (orig, new List<BosConflictSource>());
                    map[key] = entry;
                }

                entry.Sources.Add(new BosConflictSource
                {
                    FilePath           = rule.SourceFile,
                    LineNumber         = rule.LineNumber,
                    PrecedingLine      = rule.PrecedingLine,
                    LineText           = rule.LineText,
                    FollowingLine      = rule.FollowingLine,
                    SwapTarget         = rule.SwapTarget,
                    ConditionalSection = rule.ConditionalSection,
                    SectionType        = rule.SectionType
                });
            }
        }

        List<BosConflictEntry> conflicts = [];

        foreach (var (_, (objRef, sources)) in map)
        {
            if (sources.Count < 2) continue;

            var first = sources[0].SwapTarget;
            if (sources.All(s => string.Equals(s.SwapTarget, first, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sorted = sources
                .OrderBy(s => Path.GetFileName(s.FilePath), StringComparer.Ordinal)
                .ToList();

            var entry = new BosConflictEntry { ObjectRef = objRef, Sources = sorted };

            // Enrich display name from the library when available
            if (library != null)
                entry.ResolvedName = library.GetDisplayName(objRef.NormalizedKey);

            conflicts.Add(entry);
        }

        conflicts.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return new BosConflictSummary { SwapConflicts = conflicts };
    }
}
