using SkyScope.Models;

namespace SkyScope.UI;

// Files tab: SkyPatcher sub-tab (default) and SPID sub-tab, each a ConfigFileListView scoped to
// that tool's own files and conflicts.
public partial class FilesConflictView : ConflictViewBase
{
    public FilesConflictView()
    {
        InitializeComponent();
    }

    public void Clear()
    {
        SkyPatcherFilesView.Clear();
        SpidFilesView.Clear();
    }

    public void Populate(string[] skyPatcherFiles, string[] spidFiles, ConflictSummary summary)
    {
        SkyPatcherFilesView.Populate(skyPatcherFiles, summary, "SkyPatcher", "No SkyPatcher .ini files found.");
        SpidFilesView.Populate(spidFiles, summary, "SPID", "No SPID _DISTR.ini files found.");
    }
}
