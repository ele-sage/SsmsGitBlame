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
    public class GitSearchToolWindow : BaseToolWindow<GitSearchToolWindow>
    {
        private GitSearchToolWindowControl _control;

        public override string GetTitle(int toolWindowId) => "Git Search";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            _control = new GitSearchToolWindowControl();
            return Task.FromResult<FrameworkElement>(_control);
        }

        public static async Task ShowWithSearchQueryAsync(string query = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            ToolWindowPane window = await ShowAsync();
            if (window?.Content is GitSearchToolWindowControl control && !string.IsNullOrWhiteSpace(query))
            {
                control.SetSearchTextAndExecute(query);
            }
        }

        [Guid("d8164bc3-5c12-4fb0-b6ee-65a17f8562b4")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Search;
            }
        }
    }
}