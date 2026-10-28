using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [Command(PackageIds.ShowHistoryCommand)]
    internal sealed class ShowGitHistoryCommand : BaseCommand<ShowGitHistoryCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView == null)
            {
                await VS.StatusBar.ShowMessageAsync("Git History: No active SQL editor window found.");
                return;
            }

            string sqlText = docView.TextView.TextSnapshot.GetText();
            await GitHistoryToolWindow.ShowForSqlTextAsync(sqlText);
        }
    }
}