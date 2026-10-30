using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [Command(PackageIds.GlobalGitSearch)]
    internal sealed class ShowGitSearchCommand : BaseCommand<ShowGitSearchCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string query = null;
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView != null && !docView.TextView.Selection.IsEmpty)
            {
                query = docView.TextView.Selection.StreamSelectionSpan.GetText().Trim();
            }

            await GitSearchToolWindow.ShowWithSearchQueryAsync(query);
        }
    }
}