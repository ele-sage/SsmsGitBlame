using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Span = Microsoft.VisualStudio.Text.Span;

namespace SsmsGitBlame
{
    public partial class GitSearchToolWindowControl : UserControl
    {
        private readonly GitBlameService _gitService = new GitBlameService();
        private readonly GitSearchService _searchService = new GitSearchService();
        private readonly ToolkitThreadHelper _threadHelper = ToolkitThreadHelper.Create();

        // Native editor services for Code Preview
        private ITextBufferFactoryService _bufferFactory;
        private ITextEditorFactoryService _editorFactoryService;
        private IContentType _sqlContentType;
        private ITextBuffer _previewBuffer;
        private IWpfTextViewHost _previewHost;
        private IWpfTextView _previewTextView;

        private CancellationTokenSource _searchCts;
        private CancellationTokenSource _previewLoadCts;
        private readonly DispatcherTimer _debounceTimer;
        private string _activeRepoRoot;
        private string _currentlyLoadedPreviewPath;
        private bool _suppressTextChangedSearch;

        public GitSearchToolWindowControl()
        {
            InitializeComponent();
            ThreadHelper.ThrowIfNotOnUIThread();

            InitializeCodePreviewEditor();

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                ExecuteSearch();
            };

            GitRepositoryConfigService.RepositoryChanged += OnRepositoryChanged;
            PreviewKeyDown += OnControlPreviewKeyDown;
            Unloaded += OnControlUnloaded;
            Loaded += OnControlLoaded;
        }

        private void InitializeCodePreviewEditor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (componentModel == null) return;

            _bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
            _editorFactoryService = componentModel.GetService<ITextEditorFactoryService>();
            var contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();

            _sqlContentType = contentTypeRegistry.GetContentType("SQL Server Tools")
                           ?? contentTypeRegistry.GetContentType("SQL")
                           ?? contentTypeRegistry.GetContentType("text");

            _previewBuffer = _bufferFactory.CreateTextBuffer("", _sqlContentType);

            var roles = _editorFactoryService.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Interactive,
                PredefinedTextViewRoles.Document,
                PredefinedTextViewRoles.Structured);

            _previewTextView = _editorFactoryService.CreateTextView(_previewBuffer, roles);
            _previewTextView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
            _previewTextView.Options.SetOptionValue(DefaultTextViewOptions.IsViewportLeftClippedId, true);

            _previewHost = _editorFactoryService.CreateTextViewHost(_previewTextView, setFocus: false);

            CodePreviewHost.Child = _previewHost.HostControl;
        }

        private void OnRepositoryChanged(object sender, EventArgs e)
        {
            _threadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshRepositoryContext();
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    ExecuteSearch();
                }
            }).FileAndForget(nameof(OnRepositoryChanged));
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            RefreshRepositoryContext();
            SearchBox.Focus();
        }

        public void SetSearchTextAndExecute(string query)
        {
            _debounceTimer.Stop();
            _suppressTextChangedSearch = true;
            try
            {
                SearchBox.Text = query ?? "";
                ClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            finally
            {
                _suppressTextChangedSearch = false;
            }

            SearchBox.SelectAll();
            SearchBox.Focus();
            ExecuteSearch();
        }

        private void RefreshRepositoryContext()
        {
            string configuredRepo = _gitService.GetEffectiveRepoRoot(null);
            _activeRepoRoot = configuredRepo;

            if (string.IsNullOrEmpty(_activeRepoRoot) || !Directory.Exists(_activeRepoRoot))
            {
                RepoPathTextBlock.Text = "No Git repository configured. Click 'Repo...' to set one.";
            }
            else
            {
                RepoPathTextBlock.Text = _activeRepoRoot;
            }
        }

        private void OnControlPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                switch (e.SystemKey)
                {
                    case Key.C:
                        MatchCaseToggle.IsChecked = !(MatchCaseToggle.IsChecked == true);
                        ExecuteSearch();
                        e.Handled = true;
                        break;
                    case Key.W:
                        WholeWordToggle.IsChecked = !(WholeWordToggle.IsChecked == true);
                        ExecuteSearch();
                        e.Handled = true;
                        break;
                    case Key.X:
                        RegexToggle.IsChecked = !(RegexToggle.IsChecked == true);
                        ExecuteSearch();
                        e.Handled = true;
                        break;
                }
            }
        }

        private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            ClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (_suppressTextChangedSearch) return;

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnClearButtonClick(object sender, RoutedEventArgs e)
        {
            _debounceTimer.Stop();
            CancelAndResetSearchCts();
            SearchBox.Text = "";
            SearchBox.Focus();
            ResultsListBox.ItemsSource = null;
            MatchStatsTextBlock.Text = "0 matches";
            StatusTextBlock.Text = "Ready";
        }

        private void OnToggleOptionClick(object sender, RoutedEventArgs e) => ExecuteSearch();

        private void OnSearchTriggerKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_debounceTimer.IsEnabled)
                {
                    _debounceTimer.Stop();
                    ExecuteSearch();
                }
                else if (ResultsListBox.SelectedItem != null)
                {
                    OpenSelectedInEditor();
                }
                else
                {
                    ExecuteSearch();
                }
            }
            else if (e.Key == Key.Down && ResultsListBox.Items.Count > 0)
            {
                e.Handled = true;
                if (ResultsListBox.SelectedIndex < ResultsListBox.Items.Count - 1)
                {
                    ResultsListBox.SelectedIndex++;
                    ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
                }
            }
            else if (e.Key == Key.Up && ResultsListBox.Items.Count > 0)
            {
                e.Handled = true;
                if (ResultsListBox.SelectedIndex > 0)
                {
                    ResultsListBox.SelectedIndex--;
                    ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
                }
            }
        }

        private void ExecuteSearch()
        {
            string pattern = SearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                CancelAndResetSearchCts();
                ResultsListBox.ItemsSource = null;
                MatchStatsTextBlock.Text = "0 matches";
                StatusTextBlock.Text = "Ready";
                return;
            }

            if (string.IsNullOrEmpty(_activeRepoRoot) || !Directory.Exists(_activeRepoRoot))
            {
                RefreshRepositoryContext();
                if (string.IsNullOrEmpty(_activeRepoRoot)) return;
            }

            CancelAndResetSearchCts();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            bool matchCase = MatchCaseToggle.IsChecked == true;
            bool matchWholeWord = WholeWordToggle.IsChecked == true;
            bool useRegex = RegexToggle.IsChecked == true;
            string filePattern = FileMaskCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(FilePatternBox.Text)
                ? FilePatternBox.Text.Trim()
                : "*";

            StatusTextBlock.Text = "Searching...";

            _threadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    GitSearchResponse response = await Task.Run(
                        () => _searchService.SearchAsync(
                            _activeRepoRoot,
                            pattern,
                            matchCase,
                            matchWholeWord,
                            useRegex,
                            filePattern,
                            token),
                        token);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (token.IsCancellationRequested) return;

                    var results = response.Items;
                    ResultsListBox.ItemsSource = results;

                    string countSuffix = response.IsTruncated ? $"{results.Count}+" : $"{results.Count}";
                    MatchStatsTextBlock.Text = $"{countSuffix} matches";
                    StatusTextBlock.Text = response.IsTruncated
                        ? $"Showing first {results.Count} matches (refine query for more)."
                        : $"{results.Count} results found.";

                    if (results.Count > 0)
                    {
                        ResultsListBox.SelectedIndex = 0;
                        ResultsListBox.ScrollIntoView(results[0]);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Search superseded or cancelled
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!token.IsCancellationRequested)
                    {
                        StatusTextBlock.Text = $"Error: {ex.Message}";
                    }
                }
            }).FileAndForget(nameof(ExecuteSearch));
        }

        private void OnResultSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ResultsListBox.SelectedItem is not SearchResultItem selected) return;

            PreviewFileNameText.Text = selected.FileName;
            PreviewFileDirText.Text = selected.DirectoryPath;
            UpdatePreviewCodeAsync(selected).FileAndForget(nameof(UpdatePreviewCodeAsync));
        }

        private async Task UpdatePreviewCodeAsync(SearchResultItem selected)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_previewBuffer == null || _previewTextView == null || _previewTextView.IsClosed)
                return;

            CancelAndResetPreviewCts();
            _previewLoadCts = new CancellationTokenSource();
            CancellationToken token = _previewLoadCts.Token;

            string filePath = selected.FullPath;
            int targetLine1Based = selected.LineNumber;

            try
            {
                if (!string.Equals(_currentlyLoadedPreviewPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    string content = await Task.Run(() =>
                        File.Exists(filePath) ? File.ReadAllText(filePath) : null, token);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (token.IsCancellationRequested || content == null) return;

                    using (ITextEdit edit = _previewBuffer.CreateEdit())
                    {
                        edit.Replace(new Span(0, _previewBuffer.CurrentSnapshot.Length), content);
                        edit.Apply();
                    }
                    _currentlyLoadedPreviewPath = filePath;
                }

                var snapshot = _previewTextView.TextSnapshot;
                if (snapshot.LineCount == 0) return;

                int lineIndex = Math.Max(0, Math.Min(targetLine1Based - 1, snapshot.LineCount - 1));
                ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineIndex);

                // Highlight the matched substring inside the target line if possible
                SnapshotSpan targetSpan = new SnapshotSpan(line.Start, line.End);
                if (selected.HighlightRegex != null)
                {
                    string lineText = line.GetText();
                    Match match = selected.HighlightRegex.Match(lineText);
                    if (match.Success && match.Length > 0)
                    {
                        var matchSpan = new SnapshotSpan(line.Start + match.Index, match.Length);
                        _previewTextView.Selection.Select(matchSpan, isReversed: false);
                        _previewTextView.Caret.MoveTo(matchSpan.End);
                        targetSpan = matchSpan;
                    }
                    else
                    {
                        _previewTextView.Selection.Clear();
                        _previewTextView.Caret.MoveTo(line.Start);
                    }
                }
                else
                {
                    _previewTextView.Selection.Clear();
                    _previewTextView.Caret.MoveTo(line.Start);
                }

                _previewTextView.ViewScroller.EnsureSpanVisible(
                    targetSpan,
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void OnResultMouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedInEditor();

        private void OnResultsListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSelectedInEditor();
            }
        }

        private void OnOpenSelectedInEditorClick(object sender, RoutedEventArgs e) => OpenSelectedInEditor();

        private void OpenSelectedInEditor()
        {
            if (ResultsListBox.SelectedItem is not SearchResultItem selected) return;

            _threadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SsmsObjectOpenerService.OpenSearchResultForModifyAsync(selected);
            }).FileAndForget(nameof(OpenSelectedInEditor));
        }

        private void OnSelectRepoButtonClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string chosen = GitRepositoryConfigService.PromptUserForRepository(null);
            if (!string.IsNullOrEmpty(chosen))
            {
                _activeRepoRoot = chosen;
                RepoPathTextBlock.Text = _activeRepoRoot;
                ExecuteSearch();
            }
        }

        private void CancelAndResetSearchCts()
        {
            var cts = Interlocked.Exchange(ref _searchCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
            }
        }

        private void CancelAndResetPreviewCts()
        {
            var cts = Interlocked.Exchange(ref _previewLoadCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
            }
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _debounceTimer.Stop();
            CancelAndResetSearchCts();
            CancelAndResetPreviewCts();
        }
    }
}