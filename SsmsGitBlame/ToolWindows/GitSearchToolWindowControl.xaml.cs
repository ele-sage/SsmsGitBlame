using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SsmsGitBlame.Services;
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
        private DispatcherTimer _debounceTimer;
        private string _activeRepoRoot;
        private string _currentlyLoadedPreviewPath;

        public GitSearchToolWindowControl()
        {
            InitializeComponent();
            ThreadHelper.ThrowIfNotOnUIThread();

            InitializeCodePreviewEditor();

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                ExecuteSearch();
            };

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
                PredefinedTextViewRoles.Document);

            // Create standard text view, then wrap in a WPF host to obtain the visual element
            _previewTextView = _editorFactoryService.CreateTextView(_previewBuffer, roles);
            _previewHost = _editorFactoryService.CreateTextViewHost(_previewTextView, setFocus: false);

            _previewTextView.VisualElement.Focusable = false;
            _previewTextView.Background = Brushes.White;

            CodePreviewHost.Child = _previewHost.HostControl;
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
            RefreshRepositoryContext();
        }

        public void SetSearchTextAndExecute(string query)
        {
            SearchBox.Text = query;
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
                RepoPathTextBlock.Text = "No Git repository configured. Click '📁 Repo...' to set one.";
            }
            else
            {
                RepoPathTextBlock.Text = _activeRepoRoot;
            }
        }

        private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            ClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnClearButtonClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
            ResultsListBox.ItemsSource = null;
            MatchStatsTextBlock.Text = "0 matches";
        }

        private void OnToggleOptionClick(object sender, RoutedEventArgs e) => ExecuteSearch();

        private void OnSearchTriggerKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _debounceTimer.Stop();
                ExecuteSearch();
            }
            else if (e.Key == Key.Down && ResultsListBox.Items.Count > 0)
            {
                e.Handled = true;
                ResultsListBox.Focus();
                ResultsListBox.SelectedIndex = 0;
            }
        }

        private void ExecuteSearch()
        {
            string pattern = SearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
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

            _searchCts?.Cancel();
            _searchCts?.Dispose();
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
                    List<SearchResultItem> results = await Task.Run(
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

                    ResultsListBox.ItemsSource = results;
                    MatchStatsTextBlock.Text = $"{results.Count} matches";
                    StatusTextBlock.Text = $"{results.Count} results found.";

                    if (results.Count > 0)
                    {
                        ResultsListBox.SelectedIndex = 0;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusTextBlock.Text = $"Error: {ex.Message}";
                }
            }).FileAndForget(nameof(ExecuteSearch));
        }

        // Fully qualified to avoid CS0104 ambiguity
        private void OnResultSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ResultsListBox.SelectedItem is not SearchResultItem selected) return;

            PreviewFileNameText.Text = selected.FileName;
            PreviewFileDirText.Text = selected.DirectoryPath;

            UpdatePreviewCode(selected.FullPath, selected.LineNumber);
        }

        private void UpdatePreviewCode(string filePath, int targetLine1Based)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(filePath) || _previewBuffer == null) return;

            try
            {
                if (_currentlyLoadedPreviewPath != filePath)
                {
                    string content = File.ReadAllText(filePath);
                    using (ITextEdit edit = _previewBuffer.CreateEdit())
                    {
                        // Using aliased Span to avoid ambiguous reference with System.Windows.Documents.Span
                        edit.Replace(new Span(0, _previewBuffer.CurrentSnapshot.Length), content);
                        edit.Apply();
                    }
                    _currentlyLoadedPreviewPath = filePath;
                }

                var snapshot = _previewTextView.TextSnapshot;
                int lineIndex = Math.Max(0, Math.Min(targetLine1Based - 1, snapshot.LineCount - 1));
                ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineIndex);

                _previewTextView.Caret.MoveTo(line.Start);
                _previewTextView.ViewScroller.EnsureSpanVisible(
                    new SnapshotSpan(line.Start, line.End),
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }
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
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!File.Exists(selected.FullPath)) return;

                DocumentView docView = await VS.Documents.OpenAsync(selected.FullPath);
                if (docView?.TextView != null)
                {
                    var snapshot = docView.TextView.TextSnapshot;
                    int lineIndex = Math.Max(0, Math.Min(selected.LineNumber - 1, snapshot.LineCount - 1));
                    ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineIndex);

                    docView.TextView.Caret.MoveTo(line.Start);
                    docView.TextView.ViewScroller.EnsureSpanVisible(
                        new SnapshotSpan(line.Start, line.End),
                        EnsureSpanVisibleOptions.AlwaysCenter);
                }
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

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _previewHost?.Close();
        }
    }
}