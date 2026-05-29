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
}
