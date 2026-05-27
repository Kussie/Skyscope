using System;
using System.Collections.Generic;
using System.IO;
using SkyScope.Models;

namespace SkyScope.Core;

public class BosConfigParser
{
    public (List<BosSwapRule> Rules, int FilesScanned) LoadSwapRulesFromDirectory(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return (new(), 0);

        var files = Directory.GetFiles(dataPath, "*_SWAP.ini", SearchOption.AllDirectories);
        var rules = new List<BosSwapRule>();

        foreach (var filePath in files)
        {
            try { rules.AddRange(ParseFile(filePath)); }
            catch { }
        }

        return (rules, files.Length);
    }

    private static IEnumerable<BosSwapRule> ParseFile(string filePath)
    {
        var lines          = File.ReadAllLines(filePath);
        var currentSection = "Forms";
        string? currentCondition = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line    = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed[0] == ';') continue;

            // Section header: [Forms], [References], [Forms|LocationEDID,...], etc.
            if (trimmed[0] == '[')
            {
                var close = trimmed.IndexOf(']');
                if (close < 0) continue;

                var inner    = trimmed[1..close];
                var pipeIdx  = inner.IndexOf('|');

                if (pipeIdx >= 0)
                {
                    currentSection   = inner[..pipeIdx].Trim();
                    currentCondition = inner[(pipeIdx + 1)..].Trim();
                    if (string.IsNullOrEmpty(currentCondition)) currentCondition = null;
                }
                else
                {
                    currentSection   = inner.Trim();
                    currentCondition = null;
                }
                continue;
            }

            // Swap rule line: origBaseID[,orig2]|swapBaseID[,swap2]|properties|chance
            var fields = trimmed.Split('|');
            if (fields.Length < 2) continue;

            var origField   = fields[0].Trim();
            var swapTarget  = fields[1].Trim();

            if (string.IsNullOrEmpty(origField) || string.IsNullOrEmpty(swapTarget)) continue;

            var origObjects = new List<BosObjectRef>();
            foreach (var raw in origField.Split(','))
            {
                var f   = raw.Trim();
                if (string.IsNullOrEmpty(f)) continue;
                var obj = ParseObjectRef(f);
                if (obj != null) origObjects.Add(obj);
            }

            if (origObjects.Count == 0) continue;

            string? preceding = i > 0               ? lines[i - 1] : null;
            string? following = i < lines.Length - 1 ? lines[i + 1] : null;

            yield return new BosSwapRule
            {
                OriginalObjects   = origObjects,
                SwapTarget        = swapTarget,
                SourceFile        = filePath,
                LineNumber        = i + 1,
                LineText          = line,
                PrecedingLine     = preceding,
                FollowingLine     = following,
                ConditionalSection = currentCondition,
                SectionType       = currentSection
            };
        }
    }

    private static BosObjectRef? ParseObjectRef(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        // 0xFormId~Plugin.esp  →  FormIdWithPlugin
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var tilde = token.IndexOf('~');
            if (tilde > 0)
            {
                var hexPart    = token[2..tilde].Trim();
                var pluginPart = token[(tilde + 1)..].Trim();
                if (!string.IsNullOrEmpty(hexPart) && !string.IsNullOrEmpty(pluginPart))
                    return new BosObjectRef { RefType = BosRefType.FormIdWithPlugin, Plugin = pluginPart, FormId = hexPart };
            }

            // Bare 0x...
            return new BosObjectRef { RefType = BosRefType.BareHex, Identifier = token };
        }

        // Decimal integer — not a meaningful identifier, skip
        if (uint.TryParse(token, out _)) return null;

        // Plain EditorId
        return new BosObjectRef { RefType = BosRefType.EditorId, Identifier = token };
    }
}
