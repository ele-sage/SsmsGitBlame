using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SsmsGitBlame
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class SqlGitBlameViewCreationListener : IWpfTextViewCreationListener
    {
#pragma warning disable 0169
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("SsmsGitBlameLayer")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        private AdornmentLayerDefinition _blameLayerDefinition;
#pragma warning restore 0169

        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlGitBlameAdornmentManager),
                () => new SqlGitBlameAdornmentManager(textView));
        }
    }

    internal sealed class SqlGitBlameAdornmentManager
    {
        private static SqlGitBlameAdornmentManager _lastFocusedManager;

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly GitBlameService _blameService;
        private readonly DispatcherTimer _debounceTimer;

        private Dictionary<int, LineBlameViewModel> _lineBlameCache = new Dictionary<int, LineBlameViewModel>();
        private CancellationTokenSource _cts;
        private bool _isBlameEnabled = false;

        public bool IsBlameEnabled => _isBlameEnabled;

        public static bool IsActiveViewBlameEnabled()
        {
            return _lastFocusedManager != null && _lastFocusedManager._isBlameEnabled;
        }

        public SqlGitBlameAdornmentManager(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer("SsmsGitBlameLayer");
            _blameService = new GitBlameService();

            _debounceTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _debounceTimer.Tick += OnDebounceTick;

            _view.GotAggregateFocus += OnViewGotFocus;
            _view.TextBuffer.Changed += OnTextBufferChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;

            _lastFocusedManager = this;
        }

        private void OnViewGotFocus(object sender, EventArgs e)
        {
            _lastFocusedManager = this;
        }

        public void ToggleBlame()
        {
            _isBlameEnabled = !_isBlameEnabled;

            if (_isBlameEnabled)
            {
                _debounceTimer.Stop();
                OnDebounceTick(this, EventArgs.Empty);
            }
            else
            {
                _debounceTimer.Stop();
                _cts?.Cancel();
                _lineBlameCache.Clear();
                _layer.RemoveAllAdornments();
            }
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (!_isBlameEnabled) return;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            if (_view.IsClosed || !_isBlameEnabled) return;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ITextSnapshot snapshot = _view.TextSnapshot;
            if (snapshot.Length <= 20) return;

            string sqlText = snapshot.GetText();

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var newCache = await Task.Run(() => _blameService.LoadGitBlameForSqlAsync(sqlText, token), token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!token.IsCancellationRequested && !_view.IsClosed && _isBlameEnabled && newCache != null)
                {
                    _lineBlameCache = newCache;
                    RenderAllVisibleLines();
                }
            }).FileAndForget(nameof(OnDebounceTick));
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!_isBlameEnabled || _lineBlameCache.Count == 0) return;

            foreach (ITextViewLine line in e.NewOrReformattedLines)
            {
                if (line is IWpfTextViewLine wpfLine)
                {
                    CreateAdornmentForLine(wpfLine);
                }
            }
        }

        private void RenderAllVisibleLines()
        {
            _layer.RemoveAllAdornments();
            if (!_isBlameEnabled || _lineBlameCache.Count == 0 || _view.TextViewLines == null) return;

            foreach (IWpfTextViewLine wpfLine in _view.TextViewLines.WpfTextViewLines)
            {
                CreateAdornmentForLine(wpfLine);
            }
        }

        private void CreateAdornmentForLine(IWpfTextViewLine line)
        {
            int zeroBasedLine = _view.TextSnapshot.GetLineNumberFromPosition(line.Start);

            if (_lineBlameCache.TryGetValue(zeroBasedLine, out LineBlameViewModel vm))
            {
                _layer.RemoveAdornmentsByVisualSpan(line.Extent);

                vm.EditorFontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily;
                vm.EditorFontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize * 0.88;

                var control = new BlameTagControl
                {
                    DataContext = vm
                };
                control.TagClicked += (s, clickedVm) => OnBlameTagClicked(clickedVm);

                Canvas.SetLeft(control, line.Right + 24);
                Canvas.SetTop(control, line.Top);

                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, null, control, null);
            }
        }

        // Clicking an inline blame tag opens the bottom Git History Tool Window directly to that commit & line!
        private void OnBlameTagClicked(LineBlameViewModel vm)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_view.IsClosed) return;

                string sqlText = _view.TextSnapshot.GetText();
                await GitHistoryToolWindow.ShowForSqlTextAsync(
                    sqlText: sqlText,
                    targetCommitSha: vm.CommitSha,
                    targetGitLineNumber: vm.GitLineNumber);
            }).FileAndForget(nameof(OnBlameTagClicked));
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            if (ReferenceEquals(_lastFocusedManager, this))
            {
                _lastFocusedManager = null;
            }
            _debounceTimer.Stop();
            _debounceTimer.Tick -= OnDebounceTick;
            _view.GotAggregateFocus -= OnViewGotFocus;
            _view.TextBuffer.Changed -= OnTextBufferChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}