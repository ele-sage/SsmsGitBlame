using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [Command(PackageIds.ShowSelectionHistoryCommand)]
    internal sealed class ShowSelectionHistoryCommand : BaseCommand<ShowSelectionHistoryCommand>
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

            IWpfTextView textView = docView.TextView;
            string sqlText = textView.TextSnapshot.GetText();

            int startLine1Based;
            int endLine1Based;

            if (!textView.Selection.IsEmpty)
            {
                startLine1Based = textView.TextSnapshot.GetLineNumberFromPosition(textView.Selection.Start.Position) + 1;
                endLine1Based = textView.TextSnapshot.GetLineNumberFromPosition(textView.Selection.End.Position) + 1;
            }
            else
            {
                // If no selection, use the current line where the caret sits
                int caretLine = textView.TextSnapshot.GetLineNumberFromPosition(textView.Caret.Position.BufferPosition) + 1;
                startLine1Based = caretLine;
                endLine1Based = caretLine;
            }

            await GitHistoryToolWindow.ShowForSqlTextAsync(
                sqlText,
                selectionStartEditorLine1Based: startLine1Based,
                selectionEndEditorLine1Based: endLine1Based);
        }
    }
}