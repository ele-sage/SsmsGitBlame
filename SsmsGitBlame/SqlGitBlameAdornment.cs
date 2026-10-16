using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace SsmsGitBlame
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL Server Tools")] // Targets SSMS SQL query editors specifically
    [ContentType("SQL")]              // Fallback for standard SQL content types
    [ContentType("text")]             // Remove "text" once you verify the SQL content type works in your SSMS version
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
            textView.Properties.GetOrCreateSingletonProperty(() => new SqlGitBlameAdornmentManager(textView));
        }
    }

    internal sealed class SqlGitBlameAdornmentManager
    {
        // 1. Pre-compile Regexes and Freeze Brushes once in memory
        private static readonly Regex DbRegex = new Regex(
            @"USE\s+\[?(?<db>[^\]\r\n\s;]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ObjRegex = new Regex(
            @"(?<kw>ALTER|CREATE(?:\s+OR\s+ALTER)?)\s+(?<type>PROC|PROCEDURE|VIEW|FUNCTION)\s+(?:\[?(?<schema>[^\]\.\s]+)\]?\.)?\[?(?<name>[^\]\s\(]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GitObjRegex = new Regex(
            @"(?<kw>ALTER|CREATE(?:\s+OR\s+ALTER)?)\s+(?:PROC|PROCEDURE|VIEW|FUNCTION)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Brush UncommittedBrush = CreateFrozenBrush(100, 160, 210);
        private static readonly Brush CommittedBrush = CreateFrozenBrush(140, 140, 140);

        private static Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly string _repoRoot =
            Environment.GetEnvironmentVariable("SSMS_GIT_BLAME_REPO")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"source\repos\VialisR");
        private readonly DispatcherTimer _debounceTimer;

        private Dictionary<int, string> _lineBlameCache = new Dictionary<int, string>();
        private CancellationTokenSource _cts;

        // 2. Cache static file header data so we don't hit the disk on every keystroke
        private string _cachedGitFilePath;
        private string _cachedGitHeaderPrefix = "";
        private string _cachedGitKeyword = "";
        private int _cachedGitStartOffsetLine;

        public SqlGitBlameAdornmentManager(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer("SsmsGitBlameLayer");

            _debounceTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _debounceTimer.Tick += OnDebounceTick;

            _view.TextBuffer.Changed += OnTextBufferChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;

            // Trigger initial load if document already has content
            if (_view.TextSnapshot.Length > 20)
            {
                _debounceTimer.Start();
            }
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // Ignore empty/whitespace-only changes that don't affect line count if you want even less work,
            // or simply reset the debounce timer:
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async void OnDebounceTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            if (_view.IsClosed) return;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ITextSnapshot snapshot = _view.TextSnapshot;
            if (snapshot.Length <= 20) return;

            string sqlText = snapshot.GetText();

            // 3. Offload Regex, File I/O, and Git process to a background thread
            var newCache = await Task.Run(() => LoadGitBlameForBufferAsync(sqlText, snapshot, token), token);

            if (!token.IsCancellationRequested && !_view.IsClosed && newCache != null)
            {
                _lineBlameCache = newCache;
                RenderAllVisibleLines();
            }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_lineBlameCache.Count == 0) return;

            // 4. Only create adornments for lines that actually scrolled into view or reformatted!
            foreach (ITextViewLine line in e.NewOrReformattedLines)
            {
                CreateAdornmentForLine(line);
            }
        }

        private void RenderAllVisibleLines()
        {
            _layer.RemoveAllAdornments();
            if (_lineBlameCache.Count == 0 || _view.TextViewLines == null) return;

            foreach (ITextViewLine line in _view.TextViewLines)
            {
                CreateAdornmentForLine(line);
            }
        }

        private void CreateAdornmentForLine(ITextViewLine line)
        {
            int zeroBasedLine = _view.TextSnapshot.GetLineNumberFromPosition(line.Start);

            if (_lineBlameCache.TryGetValue(zeroBasedLine, out string blameInfo))
            {
                // Remove existing adornment for this specific span before adding a new one
                _layer.RemoveAdornmentsByVisualSpan(line.Extent);

                bool isUncommitted = blameInfo.StartsWith("Not Committed Yet", StringComparison.Ordinal);

                var textBlock = new TextBlock
                {
                    Text = $"    -- {blameInfo}",
                    Foreground = isUncommitted ? UncommittedBrush : CommittedBrush,
                    FontStyle = FontStyles.Italic,
                    FontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                    FontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize * 0.88,
                    IsHitTestVisible = false // Prevents mouse hit-testing overhead in WPF
                };

                Canvas.SetLeft(textBlock, line.Right + 24);
                Canvas.SetTop(textBlock, line.Top);

                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, null, textBlock, null);
            }
        }

        private async Task<Dictionary<int, string>> LoadGitBlameForBufferAsync(
            string sqlText,
            ITextSnapshot snapshot,
            CancellationToken token)
        {
            var result = new Dictionary<int, string>();

            // Limit regex search to the first 4000 characters where USE and CREATE/ALTER live
            string headerSearchSpace = sqlText.Length > 4000 ? sqlText.Substring(0, 4000) : sqlText;

            var objMatch = ObjRegex.Match(headerSearchSpace);
            if (!objMatch.Success || token.IsCancellationRequested) return result;

            var dbMatch = DbRegex.Match(headerSearchSpace);
            string dbName = dbMatch.Success ? dbMatch.Groups["db"].Value.Trim() : "d1_legos";
            string rawType = objMatch.Groups["type"].Value.ToUpperInvariant();
            string folderType = rawType.StartsWith("PROC", StringComparison.Ordinal) ? "PROCEDUREs" : rawType + "s";
            string schema = objMatch.Groups["schema"].Success ? objMatch.Groups["schema"].Value.Trim() : "dbo";
            string objName = objMatch.Groups["name"].Value.Trim();

            string gitFilePath = Path.Combine(_repoRoot, dbName, folderType, $"{schema}.{objName}.sql");

            // Only read the disk file if we haven't cached this SP's header yet
            if (!string.Equals(_cachedGitFilePath, gitFilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(gitFilePath)) return result;

                string gitFileText = File.ReadAllText(gitFilePath);
                var gitObjMatch = GitObjRegex.Match(gitFileText);

                _cachedGitFilePath = gitFilePath;
                if (gitObjMatch.Success)
                {
                    _cachedGitHeaderPrefix = gitFileText.Substring(0, gitObjMatch.Index);
                    _cachedGitStartOffsetLine = CountNewLines(_cachedGitHeaderPrefix);
                    _cachedGitKeyword = gitObjMatch.Groups["kw"].Value;
                }
                else
                {
                    _cachedGitHeaderPrefix = "";
                    _cachedGitStartOffsetLine = 0;
                    _cachedGitKeyword = "";
                }
            }

            int spStartOffsetLine = snapshot.GetLineNumberFromPosition(objMatch.Index);
            string liveSpBody = sqlText.Substring(objMatch.Index);

            if (!string.IsNullOrEmpty(_cachedGitKeyword))
            {
                string editorKeyword = objMatch.Groups["kw"].Value;
                if (!_cachedGitKeyword.Equals(editorKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    liveSpBody = _cachedGitKeyword + liveSpBody.Substring(editorKeyword.Length);
                }
            }

            string syntheticContents = _cachedGitHeaderPrefix + liveSpBody;

            var psi = new ProcessStartInfo("git", $"blame --porcelain --contents - \"{gitFilePath}\"")
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            {
                // Write stdin and read stdout concurrently to prevent pipe buffer deadlocks on large SPs
                Task writeTask = Task.Run(async () =>
                {
                    using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
                    {
                        await stdin.WriteAsync(syntheticContents).ConfigureAwait(false);
                    }
                }, token);

                var commitCache = new Dictionary<string, CommitInfo>(StringComparer.Ordinal);
                string currentSha = "";
                string previousSha = "";
                CommitInfo currentCommit = null;
                int gitLineIndex = 0;
                bool isNewCommitBlock = true;

                // Stream output line-by-line instead of allocating a giant string + Split('\n') array
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }

                    if (line.Length >= 40 && line[0] != '\t' && line.IndexOf(' ') == 40 && IsHexSha(line))
                    {
                        currentSha = line.Substring(0, 40);
                        if (!commitCache.TryGetValue(currentSha, out currentCommit))
                        {
                            currentCommit = new CommitInfo();
                            commitCache[currentSha] = currentCommit;
                        }
                        if (currentSha != previousSha)
                        {
                            isNewCommitBlock = true;
                            previousSha = currentSha;
                        }
                    }
                    else if (currentCommit != null && line.StartsWith("author ", StringComparison.Ordinal))
                    {
                        currentCommit.Author = line.Substring(7);
                    }
                    else if (currentCommit != null && line.StartsWith("author-time ", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.Substring(12), out long unixSeconds))
                        {
                            var dt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
                            currentCommit.Date = dt.ToString("yyyy-MM-dd HH:mm");
                        }
                    }
                    else if (currentCommit != null && line.StartsWith("summary ", StringComparison.Ordinal))
                    {
                        currentCommit.Summary = line.Substring(8);
                    }
                    else if (line.Length > 0 && line[0] == '\t')
                    {
                        int editorLine = spStartOffsetLine + (gitLineIndex - _cachedGitStartOffsetLine);
                        if (editorLine >= spStartOffsetLine && currentCommit != null)
                        {
                            if (currentSha.StartsWith("00000000", StringComparison.Ordinal))
                            {
                                result[editorLine] = "Not Committed Yet (Uncommitted edit in SSMS)";
                            }
                            else if (isNewCommitBlock)
                            {
                                result[editorLine] = $"{currentCommit.Date} • {currentCommit.Summary}";
                                isNewCommitBlock = false;
                            }
                        }
                        gitLineIndex++;
                    }
                }

                await writeTask.ConfigureAwait(false);
                proc.WaitForExit(1000);
            }

            return result;
        }

        private static int CountNewLines(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        private static bool IsHexSha(string line)
        {
            for (int i = 0; i < 40; i++)
            {
                char c = line[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Tick -= OnDebounceTick;
            _view.TextBuffer.Changed -= OnTextBufferChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private sealed class CommitInfo
        {
            public string Author = "";
            public string Date = "";
            public string Summary = "";
        }
    }
}