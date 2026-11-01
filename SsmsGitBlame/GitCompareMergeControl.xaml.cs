using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Span = Microsoft.VisualStudio.Text.Span;

namespace SsmsGitBlame
{
    public partial class GitCompareMergeControl : UserControl
    {
        private readonly ToolkitThreadHelper _threadHelper = ToolkitThreadHelper.Create();

        private ITextBufferFactoryService _bufferFactory;
        private IDifferenceBufferFactoryService _diffBufferFactory;
        private IWpfDifferenceViewerFactoryService _diffViewerFactory;
        private ITextUndoHistoryRegistry _undoHistoryRegistry;
        private IContentType _sqlContentType;

        private ITextBuffer _leftBuffer;
        private ITextBuffer _rightBuffer;
        private IDifferenceBuffer _diffBuffer;
        private IWpfDifferenceViewer _diffViewer;

        private ITextBuffer _linkedLiveEditorBuffer;
        private bool _isSyncingBuffers;

        public GitCompareMergeControl()
        {
            InitializeComponent();
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeMergeViewer();
            KeyDown += OnMergeControlKeyDown;
            Unloaded += OnControlUnloaded;
        }

        private void InitializeMergeViewer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (componentModel == null) return;

            _bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
            _diffBufferFactory = componentModel.GetService<IDifferenceBufferFactoryService>();
            _diffViewerFactory = componentModel.GetService<IWpfDifferenceViewerFactoryService>();
            _undoHistoryRegistry = componentModel.GetService<ITextUndoHistoryRegistry>();
            var contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();

            _sqlContentType = contentTypeRegistry.GetContentType("SQL Server Tools")
                           ?? contentTypeRegistry.GetContentType("SQL")
                           ?? contentTypeRegistry.GetContentType("text");

            _leftBuffer = _bufferFactory.CreateTextBuffer("", _sqlContentType);
            _rightBuffer = _bufferFactory.CreateTextBuffer("", _sqlContentType);
            _diffBuffer = _diffBufferFactory.CreateDifferenceBuffer(_leftBuffer, _rightBuffer);

            _diffViewer = _diffViewerFactory.CreateDifferenceView(_diffBuffer);
            _diffViewer.ViewMode = DifferenceViewMode.SideBySide;

            if (_diffViewer.LeftView != null)
            {
                _diffViewer.LeftView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
                _diffViewer.LeftView.Options.SetOptionValue(DefaultTextViewOptions.IsViewportLeftClippedId, true);
                _diffViewer.LeftView.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
                _diffViewer.LeftView.LayoutChanged += OnViewLayoutChanged;
                _diffViewer.LeftView.ViewportWidthChanged += (s, e) => RenderChunkAcceptButtons();
            }

            if (_diffViewer.RightView != null)
            {
                _diffViewer.RightView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
                _diffViewer.RightView.Options.SetOptionValue(DefaultTextViewOptions.IsViewportLeftClippedId, true);
                _diffViewer.RightView.LayoutChanged += OnViewLayoutChanged;
            }

            _rightBuffer.Changed += OnRightMergeBufferChanged;
            _diffBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;

            DiffViewerHost.Child = _diffViewer.VisualElement;
            DiffViewerHost.SizeChanged += (s, e) => RenderChunkAcceptButtons();
        }

        public void LoadComparison(
            string commitSha,
            string author,
            string commitDate,
            string objectDisplayName,
            string commitSql,
            ITextBuffer liveEditorBuffer,
            string fallbackLiveSql)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_linkedLiveEditorBuffer != null)
            {
                _linkedLiveEditorBuffer.Changed -= OnLiveEditorBufferChangedExternally;
            }

            _linkedLiveEditorBuffer = liveEditorBuffer;
            string currentLocalSql = _linkedLiveEditorBuffer != null
                ? _linkedLiveEditorBuffer.CurrentSnapshot.GetText()
                : (fallbackLiveSql ?? "");

            if (_linkedLiveEditorBuffer != null)
            {
                _linkedLiveEditorBuffer.Changed += OnLiveEditorBufferChangedExternally;
                SyncStatusTextBlock.Text = "● Live-Synced with SSMS Editor (Ctrl+Z supported)";
                SyncStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(46, 160, 67));
            }
            else
            {
                SyncStatusTextBlock.Text = "○ Detached (No Active Editor)";
                SyncStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(210, 153, 34));
            }

            LeftPaneHeaderTextBlock.Text = $"{commitSha}   ({author}  •  {commitDate})";
            RightPaneHeaderTextBlock.Text = $"Local — {objectDisplayName} (Editable)";

            _isSyncingBuffers = true;
            try
            {
                using (ITextEdit editLeft = _leftBuffer.CreateEdit())
                {
                    editLeft.Replace(new Span(0, _leftBuffer.CurrentSnapshot.Length), commitSql ?? "");
                    editLeft.Apply();
                }

                using (ITextEdit editRight = _rightBuffer.CreateEdit())
                {
                    editRight.Replace(new Span(0, _rightBuffer.CurrentSnapshot.Length), currentLocalSql);
                    editRight.Apply();
                }
            }
            finally
            {
                _isSyncingBuffers = false;
            }

            // Focus right view so user can immediately type or Ctrl+Z
            _diffViewer.RightView?.VisualElement.Focus();
        }

        private void OnRightMergeBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isSyncingBuffers || _linkedLiveEditorBuffer == null) return;

            _isSyncingBuffers = true;
            try
            {
                string updatedSql = _rightBuffer.CurrentSnapshot.GetText();
                using (ITextEdit edit = _linkedLiveEditorBuffer.CreateEdit())
                {
                    edit.Replace(new Span(0, _linkedLiveEditorBuffer.CurrentSnapshot.Length), updatedSql);
                    edit.Apply();
                }
            }
            catch { }
            finally
            {
                _isSyncingBuffers = false;
            }
        }

        private void OnLiveEditorBufferChangedExternally(object sender, TextContentChangedEventArgs e)
        {
            if (_isSyncingBuffers || _linkedLiveEditorBuffer == null) return;

            _isSyncingBuffers = true;
            try
            {
                string liveSql = _linkedLiveEditorBuffer.CurrentSnapshot.GetText();
                using (ITextEdit edit = _rightBuffer.CreateEdit())
                {
                    edit.Replace(new Span(0, _rightBuffer.CurrentSnapshot.Length), liveSql);
                    edit.Apply();
                }
            }
            catch { }
            finally
            {
                _isSyncingBuffers = false;
            }
        }

        private void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
        {
            _threadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_diffViewer == null || _diffViewer.IsClosed || _diffBuffer?.CurrentSnapshotDifference == null)
                    return;

                var differences = _diffBuffer.CurrentSnapshotDifference.LineDifferences.Differences;
                int count = differences.Count;
                DiffCountTextBlock.Text = count == 1 ? "1 difference" : $"{count} differences";

                RenderChunkAcceptButtons();
            }).FileAndForget(nameof(OnSnapshotDifferenceChanged));
        }

        private void OnViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            RenderChunkAcceptButtons();
        }

        /// <summary>
        /// Renders >> for code additions/modifications and ✕ for deletions, 
        /// correctly aligning to the top of the diff block.
        /// </summary>
        private void RenderChunkAcceptButtons()
        {
            ChunkActionCanvas.Children.Clear();

            if (_diffViewer == null || _diffViewer.IsClosed || _diffBuffer?.CurrentSnapshotDifference == null)
                return;

            IWpfTextView leftView = _diffViewer.LeftView;
            IWpfTextView rightView = _diffViewer.RightView;
            if (leftView == null || leftView.IsClosed || leftView.TextViewLines == null ||
                rightView == null || rightView.IsClosed || rightView.TextViewLines == null)
                return;

            ISnapshotDifference snapshotDiff = _diffBuffer.CurrentSnapshotDifference;
            ITextSnapshot leftSnapshot = snapshotDiff.LeftBufferSnapshot;
            ITextSnapshot rightSnapshot = snapshotDiff.RightBufferSnapshot;

            double gutterX = Math.Max(40, (DiffViewerHost.ActualWidth / 2.0) - 34);
            try
            {
                GeneralTransform transform = leftView.VisualElement.TransformToAncestor(DiffViewerHost);
                Point leftViewTopRight = transform.Transform(new Point(leftView.ViewportWidth, 0));
                if (leftViewTopRight.X > 40)
                {
                    gutterX = leftViewTopRight.X - 28;
                }
            }
            catch { }

            foreach (Difference diff in snapshotDiff.LineDifferences.Differences)
            {
                bool isDeletionFromRight = diff.Left.Length == 0 && diff.Right.Length > 0;
                double yPos = -1;

                if (!isDeletionFromRight)
                {
                    // Regular chunk or modification: align to top of Left diff line
                    int leftLineNum = Math.Max(0, Math.Min(diff.Left.Start, leftSnapshot.LineCount - 1));
                    ITextSnapshotLine line = leftSnapshot.GetLineFromLineNumber(leftLineNum);
                    ITextViewLine viewLine = leftView.GetTextViewLineContainingBufferPosition(line.Start);
                    if (viewLine != null && viewLine.VisibilityState != VisibilityState.Unattached)
                    {
                        yPos = viewLine.Top - leftView.ViewportTop;
                    }
                }
                else
                {
                    // Pure addition on the Right: align to top of Right diff line
                    int rightLineNum = Math.Max(0, Math.Min(diff.Right.Start, rightSnapshot.LineCount - 1));
                    ITextSnapshotLine line = rightSnapshot.GetLineFromLineNumber(rightLineNum);
                    ITextViewLine viewLine = rightView.GetTextViewLineContainingBufferPosition(line.Start);
                    if (viewLine != null && viewLine.VisibilityState != VisibilityState.Unattached)
                    {
                        yPos = viewLine.Top - rightView.ViewportTop;
                    }
                }

                if (yPos < 0 || yPos > DiffViewerHost.ActualHeight - 20)
                    continue;

                Difference capturedDiff = diff;
                Button btn;

                if (isDeletionFromRight)
                {
                    // Red-accented ✕ button to delete local additions
                    btn = new Button
                    {
                        Content = "✕",
                        Width = 22,
                        Height = 18,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.Bold,
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(207, 34, 46)),
                        Background = new SolidColorBrush(Color.FromRgb(255, 235, 233)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(255, 129, 130)),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand,
                        ToolTip = "Revert: Remove these lines from Local Editor (Ctrl+Z to undo)"
                    };
                }
                else
                {
                    var arrowStyle = TryFindResource("JbGutterArrowButtonStyle") as Style;
                    btn = new Button
                    {
                        Content = ">>",
                        Style = arrowStyle,
                        ToolTip = "Accept this code block into Local Editor (Ctrl+Z to undo)"
                    };
                }

                btn.Click += (s, args) => ApplyDiffChunkLeftToRight(capturedDiff);

                Canvas.SetLeft(btn, gutterX);
                Canvas.SetTop(btn, yPos);
                ChunkActionCanvas.Children.Add(btn);
            }
        }

        private void ApplyDiffChunkLeftToRight(Difference diff)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_diffBuffer?.CurrentSnapshotDifference == null || _rightBuffer == null)
                return;

            ISnapshotDifference snapshotDiff = _diffBuffer.CurrentSnapshotDifference;
            ITextSnapshot leftSnap = snapshotDiff.LeftBufferSnapshot;
            ITextSnapshot rightSnap = snapshotDiff.RightBufferSnapshot;

            string leftChunkText = "";
            if (diff.Left.Length > 0)
            {
                ITextSnapshotLine startLine = leftSnap.GetLineFromLineNumber(diff.Left.Start);
                ITextSnapshotLine endLine = leftSnap.GetLineFromLineNumber(diff.Left.End - 1);
                leftChunkText = leftSnap.GetText(Span.FromBounds(startLine.Start.Position, endLine.EndIncludingLineBreak.Position));
            }

            Span rightTargetSpan;
            if (diff.Right.Length > 0)
            {
                ITextSnapshotLine rStart = rightSnap.GetLineFromLineNumber(diff.Right.Start);
                ITextSnapshotLine rEnd = rightSnap.GetLineFromLineNumber(diff.Right.End - 1);
                rightTargetSpan = Span.FromBounds(rStart.Start.Position, rEnd.EndIncludingLineBreak.Position);
            }
            else
            {
                int insertPos = diff.Right.Start < rightSnap.LineCount
                    ? rightSnap.GetLineFromLineNumber(diff.Right.Start).Start.Position
                    : rightSnap.Length;
                rightTargetSpan = new Span(insertPos, 0);
            }

            // Use ITextUndoHistory so Ctrl+Z works immediately inside the compare window
            ITextUndoHistory undoHistory = _undoHistoryRegistry?.GetHistory(_rightBuffer);
            using (ITextUndoTransaction transaction = undoHistory?.CreateTransaction("Merge Diff Chunk"))
            {
                using (ITextEdit edit = _rightBuffer.CreateEdit())
                {
                    edit.Replace(rightTargetSpan, leftChunkText);
                    edit.Apply();
                }
                transaction?.Complete();
            }

            // Restore focus to RightView so pressing Ctrl+Z right after clicking works without any clicks
            _diffViewer.RightView?.VisualElement.Focus();

            string statusMsg = diff.Left.Length == 0
                ? "SsmsGitBlame: Reverted local code block (Press Ctrl+Z to undo)."
                : "SsmsGitBlame: Merged code block >> (Press Ctrl+Z to undo).";
            VS.StatusBar.ShowMessageAsync(statusMsg).FileAndForget(nameof(ApplyDiffChunkLeftToRight));
        }

        private void OnAcceptAllLeftClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_leftBuffer == null || _rightBuffer == null) return;

            string fullCommitSql = _leftBuffer.CurrentSnapshot.GetText();

            ITextUndoHistory undoHistory = _undoHistoryRegistry?.GetHistory(_rightBuffer);
            using (ITextUndoTransaction transaction = undoHistory?.CreateTransaction("Accept All Left"))
            {
                using (ITextEdit edit = _rightBuffer.CreateEdit())
                {
                    edit.Replace(new Span(0, _rightBuffer.CurrentSnapshot.Length), fullCommitSql);
                    edit.Apply();
                }
                transaction?.Complete();
            }

            _diffViewer.RightView?.VisualElement.Focus();
            VS.StatusBar.ShowMessageAsync("SsmsGitBlame: Applied full commit SQL to Local Editor (Press Ctrl+Z to undo).").FileAndForget(nameof(OnAcceptAllLeftClick));
        }

        private void OnMergeControlKeyDown(object sender, KeyEventArgs e)
        {
            // Forward Ctrl+Z / Ctrl+Y to RightView undo history if user pressed it while hovering the toolbar/canvas
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.Z)
                {
                    ITextUndoHistory undoHistory = _undoHistoryRegistry?.GetHistory(_rightBuffer);
                    if (undoHistory != null && undoHistory.CanUndo)
                    {
                        undoHistory.Undo(1);
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Y)
                {
                    ITextUndoHistory undoHistory = _undoHistoryRegistry?.GetHistory(_rightBuffer);
                    if (undoHistory != null && undoHistory.CanRedo)
                    {
                        undoHistory.Redo(1);
                        e.Handled = true;
                    }
                }
            }
        }

        private void OnPrevDiffClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer != null && !_diffViewer.IsClosed)
                _diffViewer.ScrollToPreviousChange();
        }

        private void OnNextDiffClick(object sender, RoutedEventArgs e)
        {
            if (_diffViewer != null && !_diffViewer.IsClosed)
                _diffViewer.ScrollToNextChange();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
        }
    }
}