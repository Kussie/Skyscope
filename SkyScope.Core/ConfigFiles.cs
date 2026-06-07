using System;
using System.IO;
using System.Linq;

namespace SkyScope.Core;

internal static class ConfigFiles
{
    private const string VortexMarker = "__folder_managed_by_vortex";

    // Recursively enumerates files matching pattern under root, ordered the way SkyPatcher loads
    // them (breadth-first: a folder's files before its subfolders'), excluding Vortex's
    // __folder_managed_by_vortex marker files.
    internal static string[] Enumerate(string root, string pattern) =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(VortexMarker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, SkyPatcherLoadOrderComparer.Instance)
            .ToArray();
}
