using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    public sealed class GitCommitEntry
    {
        public string CommitSha { get; set; }
        public string ShortSha => !string.IsNullOrEmpty(CommitSha) && CommitSha.Length >= 8 ? CommitSha.Substring(0, 8) : CommitSha;
        public string ParentSha { get; set; }
        public string ShortParentSha => !string.IsNullOrEmpty(ParentSha) && ParentSha.Length >= 8 ? ParentSha.Substring(0, 8) : (CommitSha + "~1");
        public string Author { get; set; }
        public DateTime CommitDate { get; set; }
        public string DisplayDate { get; set; }
        public string Subject { get; set; }
        public string GitFilePath { get; set; }
        public string RelativeGitPath { get; set; }
        public bool IsWorkingTreeDiff { get; set; }
        public string LiveBufferSql { get; set; }
    }

    public sealed class ResolvedSqlObject
    {
        public string DbName { get; set; }
        public string Schema { get; set; }
        public string ObjectName { get; set; }
        public string FullDisplayName => $"{Schema}.{ObjectName}.sql";
        public string GitFilePath { get; set; }
        public string RelativeGitPath { get; set; }
        public string NormalizedLiveSql { get; set; }
        public string DiskGitSql { get; set; }
        public int EditorToGitLineDelta { get; set; }
    }

    public sealed class GitBlameService
    {
        private static readonly Regex DbRegex = new Regex(
            @"USE\s+\[?(?<db>[^\]\r\n\s;]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ObjRegex = new Regex(
            @"(?<kw>ALTER|CREATE(?:\s+OR\s+ALTER)?)\s+(?<type>PROC|PROCEDURE|VIEW|FUNCTION)\s+(?:\[?(?<schema>[^\]\.\s]+)\]?\.)?\[?(?<name>[^\]\s\(]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GitObjRegex = new Regex(
            @"(?<kw>ALTER|CREATE(?:\s+OR\s+ALTER)?)\s+(?:PROC|PROCEDURE|VIEW|FUNCTION)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _repoRoot;

        private string _cachedGitFilePath;
        private string _cachedGitHeaderPrefix = "";
        private string _cachedGitFullText = "";
        private string _cachedGitKeyword = "";
        private int _cachedGitStartOffsetLine;

        public string RepoRoot => _repoRoot;

        public GitBlameService(string repoRoot = null)
        {
            _repoRoot = repoRoot
                ?? Environment.GetEnvironmentVariable("SSMS_GIT_BLAME_REPO")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"source\repos\VialisR");
        }

        public ResolvedSqlObject ResolveSqlObjectFromBuffer(string sqlText)
        {
            if (string.IsNullOrWhiteSpace(sqlText) || sqlText.Length <= 20)
                return null;

            string headerSearchSpace = sqlText.Length > 4000 ? sqlText.Substring(0, 4000) : sqlText;
            var objMatch = ObjRegex.Match(headerSearchSpace);
            if (!objMatch.Success) return null;

            var dbMatch = DbRegex.Match(headerSearchSpace);
            string dbName = dbMatch.Success ? dbMatch.Groups["db"].Value.Trim() : "d1_legos";
            string rawType = objMatch.Groups["type"].Value.ToUpperInvariant();
            string folderType = rawType.StartsWith("PROC", StringComparison.Ordinal) ? "PROCEDUREs" : rawType + "s";
            string schema = objMatch.Groups["schema"].Success ? objMatch.Groups["schema"].Value.Trim() : "dbo";
            string objName = objMatch.Groups["name"].Value.Trim();

            string gitFilePath = Path.Combine(_repoRoot, dbName, folderType, $"{schema}.{objName}.sql");
            if (!File.Exists(gitFilePath))
                return null;

            string gitFileText = File.ReadAllText(gitFilePath);
            var gitObjMatch = GitObjRegex.Match(gitFileText);

            string gitHeaderPrefix = "";
            string gitKeyword = "";
            int gitStartOffsetLine = 0;
            if (gitObjMatch.Success)
            {
                gitHeaderPrefix = gitFileText.Substring(0, gitObjMatch.Index);
                gitKeyword = gitObjMatch.Groups["kw"].Value;
                gitStartOffsetLine = CountNewLines(gitHeaderPrefix);
            }

            int editorSpStartLine = CountNewLines(sqlText, objMatch.Index);
            string liveSpBody = sqlText.Substring(objMatch.Index);
            if (!string.IsNullOrEmpty(gitKeyword))
            {
                string editorKeyword = objMatch.Groups["kw"].Value;
                if (!gitKeyword.Equals(editorKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    liveSpBody = gitKeyword + liveSpBody.Substring(editorKeyword.Length);
                }
            }

            string normalizedLiveSql = gitHeaderPrefix + liveSpBody;
            string relativePath = gitFilePath.Substring(_repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');

            return new ResolvedSqlObject
            {
                DbName = dbName,
                Schema = schema,
                ObjectName = objName,
                GitFilePath = gitFilePath,
                RelativeGitPath = relativePath,
                NormalizedLiveSql = normalizedLiveSql,
                DiskGitSql = gitFileText,
                EditorToGitLineDelta = gitStartOffsetLine - editorSpStartLine
            };
        }

        public async Task<List<GitCommitEntry>> GetFileHistoryAsync(
            ResolvedSqlObject resolvedObj,
            string branchOrRef = "HEAD",
            int? gitStartLine1Based = null,
            int? gitEndLine1Based = null,
            CancellationToken token = default)
        {
            var commits = new List<GitCommitEntry>();
            if (resolvedObj == null || !File.Exists(resolvedObj.GitFilePath))
                return commits;

            bool isLineRangeQuery = gitStartLine1Based.HasValue && gitEndLine1Based.HasValue;

            if (!isLineRangeQuery && !string.Equals(
                NormalizeLineEndings(resolvedObj.DiskGitSql),
                NormalizeLineEndings(resolvedObj.NormalizedLiveSql),
                StringComparison.Ordinal))
            {
                commits.Add(new GitCommitEntry
                {
                    CommitSha = "UNCOMMITTED",
                    ParentSha = "HEAD",
                    Author = Environment.UserName + " (Live Buffer)",
                    CommitDate = DateTime.Now,
                    DisplayDate = "Uncommitted",
                    Subject = $"[Live SSMS Editor] Unsaved / uncommitted modifications to {resolvedObj.Schema}.{resolvedObj.ObjectName}",
                    GitFilePath = resolvedObj.GitFilePath,
                    RelativeGitPath = resolvedObj.RelativeGitPath,
                    IsWorkingTreeDiff = true,
                    LiveBufferSql = resolvedObj.NormalizedLiveSql
                });
            }

            string refArg = string.IsNullOrWhiteSpace(branchOrRef) ? "HEAD" : branchOrRef.Trim();
            string args;

            if (isLineRangeQuery)
            {
                int totalDiskLines = Math.Max(1, CountNewLines(resolvedObj.DiskGitSql) + 1);
                int sLine = Math.Max(1, Math.Min(gitStartLine1Based.Value, totalDiskLines));
                int eLine = Math.Max(sLine, Math.Min(gitEndLine1Based.Value, totalDiskLines));

                // git log -L <start>,<end>:<file> --no-patch outputs the exact commits that touched those lines
                args = $"log {refArg} --no-patch -n 150 --pretty=format:\"%H%x1F%P%x1F%an%x1F%at%x1F%s\" -L {sLine},{eLine}:\"{resolvedObj.RelativeGitPath}\"";
            }
            else
            {
                args = $"log {refArg} --follow -n 250 --pretty=format:\"%H%x1F%P%x1F%an%x1F%at%x1F%s\" -- \"{resolvedObj.RelativeGitPath}\"";
            }

            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        break;
                    }

                    string[] parts = line.Split('\x1F');
                    if (parts.Length < 5) continue;

                    string sha = parts[0].Trim();
                    if (sha.Length != 40 || !IsHexSha(sha)) continue;

                    string parentRaw = parts[1];
                    string firstParent = string.IsNullOrWhiteSpace(parentRaw)
                        ? ""
                        : parentRaw.Split(' ')[0];
                    string author = parts[2];
                    long.TryParse(parts[3], out long unixTime);
                    string subject = parts[4];

                    DateTime localDt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;

                    commits.Add(new GitCommitEntry
                    {
                        CommitSha = sha,
                        ParentSha = firstParent,
                        Author = author,
                        CommitDate = localDt,
                        DisplayDate = FormatJetBrainsDate(localDt),
                        Subject = subject,
                        GitFilePath = resolvedObj.GitFilePath,
                        RelativeGitPath = resolvedObj.RelativeGitPath,
                        IsWorkingTreeDiff = false
                    });
                }

                proc.WaitForExit(1000);
            }

            return commits;
        }

        public async Task<(string LeftSql, string RightSql, string ParentShortSha, string CommitShortSha)> GetCommitDiffAsync(
            GitCommitEntry entry)
        {
            if (entry == null)
                return ("", "", "", "");

            if (entry.IsWorkingTreeDiff)
            {
                string headSql = await RunGitShowAsync($"HEAD:{entry.RelativeGitPath}").ConfigureAwait(false);
                return (headSql, entry.LiveBufferSql ?? "", "HEAD", "Live Editor");
            }

            string commitSqlTask = await RunGitShowAsync($"{entry.CommitSha}:{entry.RelativeGitPath}").ConfigureAwait(false);
            string parentSqlTask = "";

            if (!string.IsNullOrEmpty(entry.ParentSha))
            {
                parentSqlTask = await RunGitShowAsync($"{entry.ParentSha}:{entry.RelativeGitPath}").ConfigureAwait(false);
            }

            return (
                parentSqlTask ?? "",
                commitSqlTask ?? "",
                string.IsNullOrEmpty(entry.ParentSha) ? "Initial" : entry.ShortParentSha,
                entry.ShortSha
            );
        }

        private static string FormatJetBrainsDate(DateTime dt)
        {
            DateTime today = DateTime.Today;
            if (dt.Date == today)
                return $"Today {dt:h:mm tt}";
            if (dt.Date == today.AddDays(-1))
                return $"Yesterday {dt:h:mm tt}";
            return dt.ToString("MM/dd/yyyy h:mm tt");
        }

        private static string NormalizeLineEndings(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Trim();
        }

        public async Task<Dictionary<int, LineBlameViewModel>> LoadGitBlameForSqlAsync(
            string sqlText,
            CancellationToken token)
        {
            var result = new Dictionary<int, LineBlameViewModel>();
            if (string.IsNullOrWhiteSpace(sqlText) || sqlText.Length <= 20)
                return result;

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

            if (!string.Equals(_cachedGitFilePath, gitFilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(gitFilePath)) return result;

                string gitFileText = File.ReadAllText(gitFilePath);
                _cachedGitFullText = gitFileText;
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

            int spStartOffsetLine = CountNewLines(sqlText, objMatch.Index);
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

            var psi = new System.Diagnostics.ProcessStartInfo("git", $"blame --porcelain --contents - \"{gitFilePath}\"")
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = System.Diagnostics.Process.Start(psi))
            {
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
                            bool isUncommitted = currentSha.StartsWith("00000000", StringComparison.Ordinal);
                            if (isUncommitted && isNewCommitBlock)
                            {
                                result[editorLine] = new LineBlameViewModel
                                {
                                    DisplayText = "    -- Not Committed Yet (Click to view in Git History)",
                                    IsUncommitted = true,
                                    CommitSha = "UNCOMMITTED",
                                    Author = "Uncommitted Changes",
                                    Summary = "Working Tree vs Git HEAD",
                                    EditorLineNumber = editorLine,
                                    GitLineNumber = gitLineIndex,
                                    GitFilePath = gitFilePath,
                                    BaselineGitSql = _cachedGitFullText,
                                    CurrentLiveSql = syntheticContents
                                };
                                isNewCommitBlock = false;
                            }
                            else if (!isUncommitted && isNewCommitBlock)
                            {
                                result[editorLine] = new LineBlameViewModel
                                {
                                    DisplayText = $"    -- {currentCommit.Date} • {currentCommit.Author} • {currentCommit.Summary}",
                                    IsUncommitted = false,
                                    CommitSha = currentSha,
                                    Author = currentCommit.Author,
                                    Date = currentCommit.Date,
                                    Summary = currentCommit.Summary,
                                    EditorLineNumber = editorLine,
                                    GitLineNumber = gitLineIndex,
                                    GitFilePath = gitFilePath,
                                    BaselineGitSql = _cachedGitFullText,
                                    CurrentLiveSql = syntheticContents
                                };
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

        public async Task<(string LeftSql, string RightSql, string LeftLabel, string RightLabel)> GetDiffContentForTagAsync(
            LineBlameViewModel vm)
        {
            if (vm.IsUncommitted || string.IsNullOrEmpty(vm.CommitSha) || vm.CommitSha.StartsWith("00000000", StringComparison.Ordinal))
            {
                return (vm.BaselineGitSql, vm.CurrentLiveSql, "Git Repo File (HEAD)", "Live SSMS Buffer (Uncommitted)");
            }

            string relativePath = vm.GitFilePath.Substring(_repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            string shortSha = vm.CommitSha.Substring(0, 8);

            string parentSql = await RunGitShowAsync($"{vm.CommitSha}~1:{relativePath}").ConfigureAwait(false);
            string commitSql = await RunGitShowAsync($"{vm.CommitSha}:{relativePath}").ConfigureAwait(false);

            if (string.IsNullOrEmpty(commitSql))
            {
                return (vm.BaselineGitSql, vm.CurrentLiveSql, "Git Repo File", "Live SSMS Buffer");
            }

            return (parentSql ?? "", commitSql, $"{shortSha}~1 (Before)", $"{shortSha} ({vm.Author} • {vm.Date})");
        }

        private async Task<string> RunGitShowAsync(string objectSpec)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"show \"{objectSpec}\"")
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                proc.WaitForExit(1000);
                return proc.ExitCode == 0 ? output : "";
            }
        }

        private static int CountNewLines(string text, int maxLength = -1)
        {
            int limit = maxLength < 0 || maxLength > text.Length ? text.Length : maxLength;
            int count = 0;
            for (int i = 0; i < limit; i++)
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

        private sealed class CommitInfo
        {
            public string Author = "";
            public string Date = "";
            public string Summary = "";
        }
    }
}