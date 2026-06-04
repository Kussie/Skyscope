using System;
using System.Collections.Generic;

namespace SkyScope.Models;

// Plugins that ship with Skyrim — base game, official DLCs, and Creation Club / Anniversary
// Edition content (which uses a "cc" prefix). Single source of truth for telling vanilla content
// apart from mods (e.g. excluding base-game records from mod-only views and plugin conflicts).
public static class VanillaPlugins
{
    private static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    public static bool IsVanilla(string? plugin)
    {
        if (string.IsNullOrEmpty(plugin)) return false;
        return BaseGame.Contains(plugin) || IsCreationClub(plugin);
    }

    // Creation Club / Anniversary Edition content, which is distributed with a "cc" filename
    // prefix. Numerous and not individually enumerable, so it is matched by prefix.
    public static bool IsCreationClub(string? plugin) =>
        !string.IsNullOrEmpty(plugin) && plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase);
}
