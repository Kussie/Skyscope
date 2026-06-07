using System;
using System.Collections.Generic;

namespace SkyScope.Core;

// Orders config-file paths the way SkyPatcher actually loads them, so the "winning" rule for a
// conflict matches in-game behaviour.
//
// SkyPatcher (npc.cpp::readConfig) walks the SkyPatcher folder *breadth-first*: it uses a FIFO
// directory queue, processing every .ini in a folder before descending into that folder's
// subfolders, relying on the filesystem's case-insensitive name order, with the last-processed
// rule winning. A flat alphabetical sort of the full paths gets this wrong whenever subfolders
// are involved — it interleaves a subfolder's files with its parent folder's files — which is
// why the winner was sometimes flagged incorrectly.
//
// Breadth-first order is equivalent to sorting by (path depth, then each path component
// case-insensitively): shallower files first (a folder's files run before its subfolders'),
// then component-by-component. This comparer reproduces that total order.
public sealed class SkyPatcherLoadOrderComparer : IComparer<string>
{
    public static readonly SkyPatcherLoadOrderComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        var ca = a.Split('\\', '/');
        var cb = b.Split('\\', '/');

        // Shallower paths (fewer components) are processed first — a folder's files run before
        // its subfolders' files, regardless of name.
        if (ca.Length != cb.Length)
            return ca.Length.CompareTo(cb.Length);

        // Same depth: compare component by component, case-insensitively (filesystem name order).
        for (int i = 0; i < ca.Length; i++)
        {
            var c = string.Compare(ca[i], cb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }
}
