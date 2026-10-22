using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [Command(PackageIds.ToggleBlameCommand)]
    internal sealed class ToggleGitBlameCommand : BaseCommand<ToggleGitBlameCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Checked = SqlGitBlameAdornmentManager.IsActiveViewBlameEnabled();
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView != null &&
                docView.TextView.Properties.TryGetProperty(typeof(SqlGitBlameAdornmentManager), out SqlGitBlameAdornmentManager manager))
            {
                manager.ToggleBlame();
                Command.Checked = manager.IsBlameEnabled;
            }
        }
    }
}