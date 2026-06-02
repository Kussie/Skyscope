using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public class BosConfigParser
{
    public (List<BosSwapRule> Rules, int FilesScanned, List<string> Errors) LoadSwapRulesFromDirectory(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return (new(), 0, []);

        var files = ConfigFiles.Enumerate(dataPath, "*_SWAP.ini");
        var rules  = new List<BosSwapRule>();
        var errors = new List<string>();

        foreach (var filePath in files)
        {
            try { rules.AddRange(ParseFile(filePath)); }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse {filePath}: {ex.Message}");
                Debug.WriteLine($"[BOS] Skipped {filePath}: {ex.Message}");
            }
        }

        return (rules, files.Length, errors);
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
            // Properties (posA/posB/bnds/flags…) contain parentheses; chance is a bare integer —
            // either can be omitted, so a single trailing field is disambiguated by content.
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

            // Trailing fields may carry properties (reference filters) and/or a chance value,
            // in any order. Bare numbers and chanceA(N)/chanceR(N) are chance; everything else
            // (posA/posB/bnds/flags/"NONE"/etc.) is the properties filter. Chance values can be
            // fractional (e.g. chanceR(7.6) is a relative weight), so parse as double using the
            // invariant culture so a comma-decimal locale still reads "7.6" correctly.
            const NumberStyles numStyle = NumberStyles.Float;
            var ci = CultureInfo.InvariantCulture;
            var propertiesFilter = "";
            double? chance       = null;
            for (int f = 2; f < fields.Length; f++)
            {
                var v = fields[f].Trim();
                if (v.Length == 0) continue;

                if (double.TryParse(v, numStyle, ci, out var n))
                {
                    chance = n;
                    continue;
                }

                if (v.StartsWith("chance", StringComparison.OrdinalIgnoreCase))
                {
                    var open = v.IndexOf('(');
                    if (open > 0)
                    {
                        var inner = v[(open + 1)..].TrimEnd(')').Trim();
                        if (double.TryParse(inner, numStyle, ci, out var cn)) chance = cn;
                    }
                    continue;
                }

                if (propertiesFilter.Length == 0) propertiesFilter = v;
            }

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
                SectionType       = currentSection,
                PropertiesFilter  = propertiesFilter,
                Chance            = chance
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
