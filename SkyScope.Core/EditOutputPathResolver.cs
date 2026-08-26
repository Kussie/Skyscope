using System.IO;

namespace SkyScope.Core;

public readonly record struct EditOutputOptions(bool Enabled, string SkyrimPath, string OutputDirectory);

// Redirects config-file edits into a user-chosen output folder instead of writing through to
// whichever mod currently provides the file (e.g. under MO2, where a plain write lands inside
// whatever mod USVFS resolves the virtual Data path to).
public static class EditOutputPathResolver
{
    public static string ResolveForEdit(string virtualFilePath, EditOutputOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.OutputDirectory))
            return virtualFilePath;

        var outputPath = GetOutputPath(virtualFilePath, options);
        if (outputPath == null) return virtualFilePath;
        if (File.Exists(outputPath)) return outputPath;

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(virtualFilePath, outputPath);
        return outputPath;
    }

    // Used when scanning/reading config files. A mod manager's own virtual filesystem (e.g. MO2's
    // USVFS) only reflects our output copy if that copy is enabled and prioritized above the
    // original in the active profile — we can't rely on that, so callers must check for the copy
    // explicitly rather than trusting the virtual path to resolve to it.
    public static string ResolveForRead(string virtualFilePath, EditOutputOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.OutputDirectory))
            return virtualFilePath;

        var outputPath = GetOutputPath(virtualFilePath, options);
        return outputPath != null && File.Exists(outputPath) ? outputPath : virtualFilePath;
    }

    private static string? GetOutputPath(string virtualFilePath, EditOutputOptions options)
    {
        var dataDir = Path.Combine(options.SkyrimPath, "Data");
        var relative = Path.GetRelativePath(dataDir, virtualFilePath);
        if (relative.StartsWith("..")) return null;

        return Path.Combine(options.OutputDirectory, relative);
    }
}
