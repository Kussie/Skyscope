using System;
using System.Collections.Generic;

namespace SkyScope.Models;

// Persisted to settings.json next to the executable. New settings sections can be added
// as additional properties over time — existing files deserialize with the new fields defaulted.
public class AppSettings
{
    // Maps an appearance-conflict plugin name to a directory containing that plugin's thumbnails.
    public Dictionary<string, string> PluginThumbnailDirectories { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Plugins whose NPC appearance overrides are excluded from appearance-conflict detection.
    // Defaults to the base game, official DLCs, and the Unofficial patches; the user can edit the
    // list in Settings. Creation Club ("cc"-prefixed) content is always excluded implicitly.
    // Existing settings.json files without this field deserialize to these defaults.
    public List<string> IgnoredAppearancePlugins { get; set; } = new()
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "Unofficial Skyrim Special Edition Patch.esp",
        "Unofficial Skyrim Creation Club Content Patch.esl",
    };

    // When true, edits are redirected into EditOutputDirectory instead of writing through to
    // whichever mod/deployment currently provides the file.
    public bool RedirectEditsEnabled { get; set; } = false;

    // Folder edited copies are written into, preserving their Data-relative path (e.g. a mod
    // folder you created under MO2's mods\, or a folder in a Vortex staging area).
    public string EditOutputDirectory { get; set; } = "";
}
