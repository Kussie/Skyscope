using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyScope.Models;

namespace SkyScope.Core;

public static class PluginPathResolver
{
    public static List<string> GetOrderedPluginPaths(string skyrimGameDirectory)
    {
        var dataDir = Path.Combine(skyrimGameDirectory, "Data");
        return GetOrderedPluginPathsInternal(skyrimGameDirectory, dataDir);
    }

    public static IReadOnlyList<string> GetOrderedPluginNames(string skyrimGameDirectory) =>
        GetOrderedPluginPaths(skyrimGameDirectory).ConvertAll(p => Path.GetFileName(p)!);

    private static List<string> GetOrderedPluginPathsInternal(string skyrimGameDirectory, string dataDir)
    {
        if (!Directory.Exists(dataDir)) return [];

        var pluginsTxt = FindPluginsTxt(skyrimGameDirectory);
        if (pluginsTxt != null)
        {
            List<string> ordered = [];
            foreach (var line in File.ReadAllLines(pluginsTxt))
            {
                var trimmed = line.TrimStart('*').Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var fullPath = Path.Combine(dataDir, trimmed);
                if (File.Exists(fullPath)) ordered.Add(fullPath);
            }

            var listed = new HashSet<string>(
                ordered.Select(p => Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            var missingEsms = Directory.GetFiles(dataDir, "*.esm")
                .Where(f => !listed.Contains(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missingEsmNames = new HashSet<string>(
                missingEsms.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);

            // Creation Club content can be enabled through Skyrim's own CC manager (Data\Skyrim.ccc)
            // without ever being written to plugins.txt by a mod manager — same implicit-master gap
            // as the core ESMs above.
            var missingCc = Directory.GetFiles(dataDir, "*.esl")
                .Concat(Directory.GetFiles(dataDir, "*.esp"))
                .Where(f => VanillaPlugins.IsCreationClub(Path.GetFileName(f))
                            && !listed.Contains(Path.GetFileName(f))
                            && !missingEsmNames.Contains(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            ordered.InsertRange(0, missingEsms);
            ordered.InsertRange(missingEsms.Count, missingCc);
            if (ordered.Count > 0) return ordered;
        }

        return Directory.GetFiles(dataDir, "*.esm").OrderBy(f => f)
            .Concat(Directory.GetFiles(dataDir, "*.esl").OrderBy(f => f))
            .Concat(Directory.GetFiles(dataDir, "*.esp").OrderBy(f => f))
            .ToList();
    }

    private static string? FindPluginsTxt(string skyrimGameDirectory)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var se = Path.Combine(local, "Skyrim Special Edition", "plugins.txt");
        if (File.Exists(se)) return se;

        var vr = Path.Combine(local, "Skyrim VR", "plugins.txt");
        if (File.Exists(vr)) return vr;

        var le = Path.Combine(local, "Skyrim", "plugins.txt");
        if (File.Exists(le)) return le;

        var gameSide = Path.Combine(skyrimGameDirectory, "plugins.txt");
        if (File.Exists(gameSide)) return gameSide;

        return null;
    }
}
