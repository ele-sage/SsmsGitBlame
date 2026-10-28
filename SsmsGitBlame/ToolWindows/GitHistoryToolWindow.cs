using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SsmsGitBlame
{
    public class GitHistoryToolWindow : BaseToolWindow<GitHistoryToolWindow>
    {
        private GitHistoryToolWindowControl _control;

        public override string GetTitle(int toolWindowId) => "Git History";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            _control = new GitHistoryToolWindowControl();
            return Task.FromResult<FrameworkElement>(_control);
        }

        public static async Task ShowForSqlTextAsync(
            string sqlText,
            int? selectionStartEditorLine1Based = null,
            int? selectionEndEditorLine1Based = null,
            string targetCommitSha = null,
            int? targetGitLineNumber = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            ToolWindowPane window = await ShowAsync();
            if (window?.Content is GitHistoryToolWindowControl control)
            {
                string titleSuffix = await control.LoadHistoryForSqlAsync(
                    sqlText,
                    selectionStartEditorLine1Based,
                    selectionEndEditorLine1Based,
                    targetCommitSha,
                    targetGitLineNumber);

                if (!string.IsNullOrEmpty(titleSuffix))
                {
                    window.Caption = $"History: {titleSuffix}";
                }
            }
        }

        [Guid("62c8f9a1-4b3e-4d91-9f32-8a7e1c4b5d09")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.History;
            }
        }
    }
}