using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SsmsGitBlame
{
    public partial class GitHistoryToolWindowControl : UserControl
    {
        private readonly GitBlameService _gitService = new GitBlameService();

        private ITextBufferFactoryService _bufferFactory;
        private IDifferenceBufferFactoryService _diffBufferFactory;
        private IWpfDifferenceViewerFactoryService _diffViewerFactory;
        private IContentType _sqlContentType;

        private ITextBuffer _leftBuffer;
        private ITextBuffer _rightBuffer;
        private IDifferenceBuffer _diffBuffer;
        private IWpfDifferenceViewer _diffViewer;

        private ResolvedSqlObject _currentResolvedObj;
        private string _lastSqlBufferText;
        private string _currentRightRevisionSql = "";
        private int? _selectionStartEditorLine;
        private int? _selectionEndEditorLine;
        private int? _pendingTargetGitLineNumber;
        private CancellationTokenSource _loadCts;

        public GitHistoryToolWindowControl()
        {
            InitializeComponent();
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeDiffViewerControl();
            Unloaded += OnControlUnloaded;
        }

        private void InitializeDiffViewerControl()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (componentModel == null) return;

            _bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
            _diffBufferFactory = componentModel.GetService<IDifferenceBufferFactoryService>();
            _diffViewerFactory = componentModel.GetService<IWpfDifferenceViewerFactoryService>();
            var contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();

            _sqlContentType = contentTypeRegistry.GetContentType("SQL Server Tools")
                           ?? contentTypeRegistry.GetContentType("SQL")
                           ?? contentTypeRegistry.GetContentType("text");

            _leftBuffer = _bufferFactory.CreateTextBuffer("", _sqlContentType);
            _rightBuffer = _bufferFactory.CreateTextBuffer("", _sqlContentType);

            _diffBuffer = _diffBufferFactory.CreateDifferenceBuffer(_leftBuffer, _rightBuffer);
            _diffViewer = _diffViewerFactory.CreateDifferenceView(_diffBuffer);

            _diffViewer.ViewMode = DifferenceViewMode.Inline;
            ApplyLightBackgroundToViews();

            _diffBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;

            DiffViewerHost.Child = _diffViewer.VisualElement;
        }

        private void ApplyLightBackgroundToViews()
        {
            var whiteBrush = Brushes.White;
            if (_diffViewer.InlineView != null) _diffViewer.InlineView.Background = whiteBrush;
            if (_diffViewer.LeftView != null) _diffViewer.LeftView.Background = whiteBrush;
            if (_diffViewer.RightView != null) _diffViewer.RightView.Background = whiteBrush;
        }

        public async Task<string> LoadHistoryForSqlAsync(
            string sqlText,
            int? selectionStartEditorLine1Based = null,
            int? selectionEndEditorLine1Based = null,
            string targetCommitSha = null,
            int? targetGitLineNumber = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _lastSqlBufferText = sqlText;
            _selectionStartEditorLine = selectionStartEditorLine1Based;
            _selectionEndEditorLine = selectionEndEditorLine1Based;
            _pendingTargetGitLineNumber = targetGitLineNumber;

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            CancellationToken token = _loadCts.Token;

            ResolvedSqlObject resolved = await Task.Run(() => _gitService.ResolveSqlObjectFromBuffer(sqlText), token);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (resolved == null)
            {
                HeaderFilePathTextBlock.Text = "Could not map active SQL script to a Git file in " + _gitService.RepoRoot;
                BreadcrumbTextBlock.Text = "Unmapped SQL Script";
                CommitListBox.ItemsSource = null;
                UpdateDiffBuffers("", "");
                return null;
            }

            _currentResolvedObj = resolved;
            HeaderFilePathTextBlock.Text = resolved.GitFilePath;

            int? gitStartLine = null;
            int? gitEndLine = null;

            if (selectionStartEditorLine1Based.HasValue && selectionEndEditorLine1Based.HasValue)
            {
                gitStartLine = Math.Max(1, selectionStartEditorLine1Based.Value + resolved.EditorToGitLineDelta);
                gitEndLine = Math.Max(gitStartLine.Value, selectionEndEditorLine1Based.Value + resolved.EditorToGitLineDelta);
                ClearSelectionFilterBtn.Content = $"✕ Lines {selectionStartEditorLine1Based}-{selectionEndEditorLine1Based}";
                ClearSelectionFilterBtn.Visibility = Visibility.Visible;
                BreadcrumbTextBlock.Text = $"VialisR > {resolved.DbName} > PROCEDUREs > {resolved.FullDisplayName} (Lines {selectionStartEditorLine1Based}-{selectionEndEditorLine1Based})";
            }
            else
            {
                ClearSelectionFilterBtn.Visibility = Visibility.Collapsed;
                BreadcrumbTextBlock.Text = $"VialisR > {resolved.DbName} > PROCEDUREs > {resolved.FullDisplayName}";
            }

            string branch = string.IsNullOrWhiteSpace(BranchTextBox.Text) ? "HEAD" : BranchTextBox.Text.Trim();
            List<GitCommitEntry> commits = await Task.Run(
                () => _gitService.GetFileHistoryAsync(resolved, branch, gitStartLine, gitEndLine, token),
                token);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (token.IsCancellationRequested) return resolved.FullDisplayName;

            CommitListBox.ItemsSource = commits;
            if (commits.Count > 0)
            {
                GitCommitEntry match = null;
                if (!string.IsNullOrEmpty(targetCommitSha))
                {
                    match = commits.FirstOrDefault(c =>
                        string.Equals(c.CommitSha, targetCommitSha, StringComparison.OrdinalIgnoreCase));
                }

                if (match != null)
                {
                    CommitListBox.SelectedItem = match;
                    CommitListBox.ScrollIntoView(match);
                }
                else
                {
                    CommitListBox.SelectedIndex = 0;
                }
            }
            else
            {
                ParentShaTextBlock.Text = "-";
                CurrentShaTextBlock.Text = "-";
                DiffCountTextBlock.Text = "0 differences";
                UpdateDiffBuffers("", resolved.DiskGitSql ?? "");
            }

            return resolved.FullDisplayName;
        }

        private void OnCommitSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!(CommitListBox.SelectedItem is GitCommitEntry selectedCommit))
                return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var (leftSql, rightSql, parentShort, commitShort) = await Task.Run(
                    () => _gitService.GetCommitDiffAsync(selectedCommit));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _currentRightRevisionSql = rightSql ?? "";
                ParentShaTextBlock.Text = parentShort;
                CurrentShaTextBlock.Text = commitShort;
                HeaderFilePathTextBlock.Text = selectedCommit.GitFilePath;

                UpdateDiffBuffers(leftSql, rightSql);
            }).FileAndForget(nameof(OnCommitSelectionChanged));
        }

        private void UpdateDiffBuffers(string leftSql, string rightSql)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_leftBuffer == null || _rightBuffer == null) return;

            using (ITextEdit editLeft = _leftBuffer.CreateEdit())
            {
                editLeft.Replace(new Span(0, _leftBuffer.CurrentSnapshot.Length), leftSql ?? "");
                editLeft.Apply();
            }

            using (ITextEdit editRight = _rightBuffer.CreateEdit())
            {
                editRight.Replace(new Span(0, _rightBuffer.CurrentSnapshot.Length), rightSql ?? "");
                editRight.Apply();
            }
        }

        private void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_diffViewer == null || _diffViewer.IsClosed || _diffBuffer.CurrentSnapshotDifference == null)
                    return;

                ApplyLightBackgroundToViews();

                ISnapshotDifference snapshotDiff = _diffBuffer.CurrentSnapshotDifference;
                var differences = snapshotDiff.LineDifferences.Differences;
                int diffCount = differences.Count;

                DiffCountTextBlock.Text = diffCount == 1 ? "1 difference" : $"{diffCount} differences";

                IWpfTextView activeView = _diffViewer.ViewMode == DifferenceViewMode.Inline
                    ? _diffViewer.InlineView
                    : _diffViewer.RightView;

                if (activeView == null) return;

                if (activeView.TextSnapshot.Length > 0)
                {
                    activeView.Caret.MoveTo(new SnapshotPoint(activeView.TextSnapshot, 0));
                }

                // If opened from an inline blame tag click or selection, scroll to that specific line
                if (_pendingTargetGitLineNumber.HasValue)
                {
                    int targetLine = _pendingTargetGitLineNumber.Value;
                    _pendingTargetGitLineNumber = null;

                    int safeLine = Math.Max(0, Math.Min(targetLine, activeView.TextSnapshot.LineCount - 1));
                    ITextSnapshotLine line = activeView.TextSnapshot.GetLineFromLineNumber(safeLine);
                    activeView.ViewScroller.EnsureSpanVisible(
                        new SnapshotSpan(line.Start, line.End),
                        EnsureSpanVisibleOptions.AlwaysCenter);
                }
                else if (diffCount > 0)
                {
                    Difference firstDiff = differences[0];
                    _diffViewer.ScrollToChange(firstDiff);
                }
                else
                {
                    activeView.ViewScroller.ScrollViewportVerticallyByLines(ScrollDirection.Down, 0);
                }
            }).FileAndForget(nameof(OnSnapshotDifferenceChanged));
        }

        private void OnCopyRevisionSqlClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentRightRevisionSql)) return;

            Clipboard.SetText(_currentRightRevisionSql);
            VS.StatusBar.ShowMessageAsync("Git History: Copied revision SQL to clipboard.").FileAndForget(nameof(OnCopyRevisionSqlClick));
        }

        private void OnRestoreToEditorClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentRightRevisionSql)) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextBuffer == null)
                {
                    await VS.StatusBar.ShowMessageAsync("Git History: No active SQL editor to restore into.");
                    return;
                }

                bool confirmed = await VS.MessageBox.ShowConfirmAsync(
                    "Restore Revision to Active Editor",
                    $"Replace the entire contents of the active SQL editor with revision {CurrentShaTextBlock.Text}?\n(You can press Ctrl+Z in the editor to undo.)");

                if (!confirmed) return;

                using (ITextEdit edit = docView.TextBuffer.CreateEdit())
                {
                    edit.Replace(new Span(0, docView.TextBuffer.CurrentSnapshot.Length), _currentRightRevisionSql);
                    edit.Apply();
                }

                await VS.StatusBar.ShowMessageAsync($"Git History: Restored revision {CurrentShaTextBlock.Text} into editor.");
            }).FileAndForget(nameof(OnRestoreToEditorClick));
        }

        private void OnClearSelectionFilterClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastSqlBufferText))
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await LoadHistoryForSqlAsync(_lastSqlBufferText, null, null);
                }).FileAndForget(nameof(OnClearSelectionFilterClick));
            }
        }

        private void OnPrevDiffClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer != null && !_diffViewer.IsClosed)
            {
                _diffViewer.ScrollToPreviousChange();
            }
        }

        private void OnNextDiffClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer != null && !_diffViewer.IsClosed)
            {
                _diffViewer.ScrollToNextChange();
            }
        }

        private void OnSideBySideModeClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer == null || _diffViewer.IsClosed) return;
            _diffViewer.ViewMode = DifferenceViewMode.SideBySide;
            ApplyLightBackgroundToViews();
            SideBySideModeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(53, 116, 240));
            InlineModeBtn.BorderBrush = Brushes.Transparent;
        }

        private void OnInlineModeClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer == null || _diffViewer.IsClosed) return;
            _diffViewer.ViewMode = DifferenceViewMode.Inline;
            ApplyLightBackgroundToViews();
            InlineModeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(53, 116, 240));
            SideBySideModeBtn.BorderBrush = Brushes.Transparent;
        }

        private void OnRefreshButtonClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastSqlBufferText))
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await LoadHistoryForSqlAsync(_lastSqlBufferText, _selectionStartEditorLine, _selectionEndEditorLine);
                }).FileAndForget(nameof(OnRefreshButtonClick));
            }
        }

        private void OnBranchTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrEmpty(_lastSqlBufferText))
            {
                e.Handled = true;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await LoadHistoryForSqlAsync(_lastSqlBufferText, _selectionStartEditorLine, _selectionEndEditorLine);
                }).FileAndForget(nameof(OnBranchTextBoxKeyDown));
            }
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
        }
    }
}