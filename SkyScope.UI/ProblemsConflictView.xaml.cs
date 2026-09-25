using SkyScope.Models;

namespace SkyScope.UI;

// Problems tab: SkyPatcher sub-tab (default) and SPID sub-tab, each a ProblemListView scoped to
// that tool's own flagged issues.
public partial class ProblemsConflictView : ConflictViewBase
{
    public ProblemsConflictView()
    {
        InitializeComponent();
    }

    public void Clear()
    {
        SkyPatcherProblemsView.Clear();
        SpidProblemsView.Clear();
    }

    public void Populate(ProblemSummary summary)
    {
        SkyPatcherProblemsView.Populate(summary.SkyPatcherProblems, "No problems found in SkyPatcher configs.");
        SpidProblemsView.Populate(summary.SpidProblems, "No problems found in SPID configs.");
    }
}
