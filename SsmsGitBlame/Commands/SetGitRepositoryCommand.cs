using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [Command(PackageIds.SetRepositoryCommand)]
    internal sealed class SetGitRepositoryCommand : BaseCommand<SetGitRepositoryCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string dbName = null;
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView != null)
            {
                string sqlText = docView.TextView.TextSnapshot.GetText();
                dbName = GitBlameService.ExtractDatabaseName(sqlText);
            }

            string chosenRepo = GitRepositoryConfigService.PromptUserForRepository(dbName);
            if (!string.IsNullOrEmpty(chosenRepo))
            {
                await VS.StatusBar.ShowMessageAsync($"SsmsGitBlame: Repository set to {chosenRepo}");
            }
        }
    }
}