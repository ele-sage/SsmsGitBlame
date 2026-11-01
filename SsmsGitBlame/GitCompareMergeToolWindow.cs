using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SsmsGitBlame
{
    public class GitCompareMergeToolWindow : BaseToolWindow<GitCompareMergeToolWindow>
    {
        private GitCompareMergeControl _control;
        private static ITextBuffer _lastActiveSqlBuffer;

        public override string GetTitle(int toolWindowId) => "Compare with Local";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            _control = new GitCompareMergeControl();
            return Task.FromResult<FrameworkElement>(_control);
        }

        public static void TrackActiveSqlBuffer(ITextBuffer buffer)
        {
            if (buffer != null)
            {
                _lastActiveSqlBuffer = buffer;
            }
        }

        public static async Task OpenAsync(
            string commitSha,
            string author,
            string commitDate,
            string objectDisplayName,
            string commitSql,
            string fallbackLiveSql)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Grab the active SQL editor buffer (or fallback to the last focused SQL editor buffer)
            DocumentView activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
            ITextBuffer targetLiveBuffer = activeDoc?.TextBuffer ?? _lastActiveSqlBuffer;

            ToolWindowPane window = await ShowAsync();
            if (window?.Content is GitCompareMergeControl control)
            {
                string shortSha = !string.IsNullOrEmpty(commitSha) && commitSha.Length >= 8
                    ? commitSha.Substring(0, 8)
                    : commitSha;

                window.Caption = $"Compare: {objectDisplayName} ({shortSha} ↔ Local)";

                control.LoadComparison(
                    commitSha: commitSha,
                    author: author,
                    commitDate: commitDate,
                    objectDisplayName: objectDisplayName,
                    commitSql: commitSql,
                    liveEditorBuffer: targetLiveBuffer,
                    fallbackLiveSql: fallbackLiveSql);
            }
        }

        [Guid("a4f9d2e1-7c8b-4e3a-9d12-5f6a8b9c0d1e")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.CompareFiles;
            }
        }
    }
}