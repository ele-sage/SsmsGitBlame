using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    public sealed class GitCommitEntry
    {
        public string RepoRoot { get; set; }
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
        public string RepoRoot { get; set; }
        public string RepoName { get; set; }
        public string DbName { get; set; }
        public string Schema { get; set; }
        public string ObjectName { get; set; }
        public string FolderType { get; set; }
        public string FullDisplayName => $"{Schema}.{ObjectName}.sql";
        public string GitFilePath { get; set; }
        public string RelativeGitPath { get; set; }
        public string NormalizedLiveSql { get; set; }
        public string DiskGitSql { get; set; }
        public int EditorToGitLineDelta { get; set; }
    }

    public sealed class GitBlameService
    {
        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> RepoFileIndexCache
            = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly string _explicitRepoRoot;

        private string _cachedGitFilePath;
        private DateTime _cachedGitFileWriteTimeUtc;
        private string _cachedGitHeaderPrefix = "";
        private string _cachedGitFullText = "";
        private string _cachedGitKeyword = "";
        private int _cachedGitStartOffsetLine;

        public string RepoRoot => GetEffectiveRepoRoot() ?? "(Not Configured)";

        static GitBlameService()
        {
            GitRepositoryConfigService.RepositoryChanged += (s, e) => RepoFileIndexCache.Clear();
        }

        public GitBlameService(string explicitRepoRoot = null)
        {
            _explicitRepoRoot = explicitRepoRoot;
        }

        public static string ExtractDatabaseName(string sqlText)
        {
            return SqlScriptDomParser.ExtractDatabaseName(sqlText);
        }

        public string GetEffectiveRepoRoot(string dbName = null, string activeDocumentPath = null)
        {
            if (!string.IsNullOrWhiteSpace(_explicitRepoRoot) && Directory.Exists(_explicitRepoRoot))
                return _explicitRepoRoot;

            return GitRepositoryConfigService.ResolveRepoRoot(dbName, activeDocumentPath);
        }

        public ResolvedSqlObject ResolveSqlObjectFromBuffer(string sqlText, string activeDocumentPath = null)
        {
            if (string.IsNullOrWhiteSpace(sqlText) || sqlText.Length <= 20)
                return null;

            ParsedSqlObjectHeader parsedEditor = SqlScriptDomParser.ParseHeader(sqlText);
            if (parsedEditor == null)
                return null;

            string dbName = parsedEditor.DatabaseName;
            string repoRoot = GetEffectiveRepoRoot(dbName, activeDocumentPath);
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return null;

            string gitFilePath = FindSqlObjectFileInRepo(repoRoot, parsedEditor);
            if (string.IsNullOrEmpty(gitFilePath) || !File.Exists(gitFilePath))
                return null;

            string relativePath = gitFilePath.Substring(repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            if (string.IsNullOrEmpty(dbName))
            {
                dbName = InferDatabaseFromRelativePath(relativePath, parsedEditor);
            }

            string gitFileText = File.ReadAllText(gitFilePath);
            ParsedSqlObjectHeader parsedGit = SqlScriptDomParser.ParseHeader(gitFileText);

            string gitHeaderPrefix = "";
            string gitVerb = "";
            int gitStartOffsetLine = 0;

            if (parsedGit != null)
            {
                gitHeaderPrefix = gitFileText.Substring(0, parsedGit.StatementStartOffset);
                gitVerb = parsedGit.DdlVerbText;
                gitStartOffsetLine = parsedGit.StatementStartLine0Based;
            }

            int editorSpStartLine = parsedEditor.StatementStartLine0Based;
            string liveSpBody = sqlText.Substring(parsedEditor.StatementStartOffset);

            if (!string.IsNullOrEmpty(gitVerb) &&
                !gitVerb.Equals(parsedEditor.DdlVerbText, StringComparison.OrdinalIgnoreCase))
            {
                liveSpBody = gitVerb + liveSpBody.Substring(parsedEditor.DdlVerbLength);
            }

            string normalizedLiveSql = gitHeaderPrefix + liveSpBody;

            return new ResolvedSqlObject
            {
                RepoRoot = repoRoot,
                RepoName = new DirectoryInfo(repoRoot).Name,
                DbName = dbName ?? new DirectoryInfo(repoRoot).Name,
                Schema = parsedEditor.SchemaName,
                ObjectName = parsedEditor.ObjectName,
                FolderType = parsedEditor.DacFxFolderName,
                GitFilePath = gitFilePath,
                RelativeGitPath = relativePath,
                NormalizedLiveSql = normalizedLiveSql,
                DiskGitSql = gitFileText,
                EditorToGitLineDelta = gitStartOffsetLine - editorSpStartLine
            };
        }

        private static string InferDatabaseFromRelativePath(string relativePath, ParsedSqlObjectHeader header)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return null;

            string firstSegment = segments[0];

            // If the first folder is a standard DACFx/SSDT schema or object-type folder, the repo is single-DB
            if (firstSegment.Equals(header.SchemaName, StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals(header.DacFxFolderName, StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals(header.LegacyFolderName, StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals("Programmability", StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return firstSegment;
        }

        private static string FindSqlObjectFileInRepo(string repoRoot, ParsedSqlObjectHeader header)
        {
            string schema = header.SchemaName;
            string objName = header.ObjectName;
            string dbName = header.DatabaseName;
            string dacFxFolder = header.DacFxFolderName;
            string legacyFolder = header.LegacyFolderName;

            string schemaDotObjFile = $"{schema}.{objName}.sql";
            string objOnlyFile = $"{objName}.sql";

            var candidates = new List<string>
            {
                // 1. Standard SSDT / DACFx SchemaObjectType layout: "{Schema}\{DacFxFolder}\{ObjectName}.sql"
                Path.Combine(repoRoot, schema, dacFxFolder, objOnlyFile),
                Path.Combine(repoRoot, schema, dacFxFolder, schemaDotObjFile),

                // 2. Standard DACFx / Redgate ObjectType layout: "{DacFxFolder}\{Schema}.{ObjectName}.sql"
                Path.Combine(repoRoot, dacFxFolder, schemaDotObjFile),
                Path.Combine(repoRoot, dacFxFolder, objOnlyFile),
                Path.Combine(repoRoot, "Programmability", dacFxFolder, schemaDotObjFile),
                Path.Combine(repoRoot, "Programmability", dacFxFolder, objOnlyFile)
            };

            // 3. Multi-Database DACFx & Legacy layouts when DatabaseName is known
            if (!string.IsNullOrEmpty(dbName))
            {
                candidates.Add(Path.Combine(repoRoot, dbName, schema, dacFxFolder, objOnlyFile));
                candidates.Add(Path.Combine(repoRoot, dbName, dacFxFolder, schemaDotObjFile));
                candidates.Add(Path.Combine(repoRoot, dbName, dacFxFolder, objOnlyFile));
                candidates.Add(Path.Combine(repoRoot, dbName, legacyFolder, schemaDotObjFile));
            }

            // 4. Root legacy folder layout
            candidates.Add(Path.Combine(repoRoot, legacyFolder, schemaDotObjFile));

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // 5. Fallback: O(1) lookup in cached repository file index
            Dictionary<string, string> index = RepoFileIndexCache.GetOrAdd(repoRoot, BuildRepoSqlFileIndex);

            if (!string.IsNullOrEmpty(dbName))
            {
                if (index.TryGetValue($"{dbName}/{schema}.{objName}.sql", out string p1) && File.Exists(p1)) return p1;
                if (index.TryGetValue($"{dbName}/{schema}/{objName}.sql", out string p2) && File.Exists(p2)) return p2;
            }

            if (index.TryGetValue($"{schema}/{objName}.sql", out string p3) && File.Exists(p3)) return p3;
            if (index.TryGetValue(schemaDotObjFile, out string p4) && File.Exists(p4)) return p4;
            if (index.TryGetValue(objOnlyFile, out string p5) && File.Exists(p5)) return p5;

            return null;
        }

        private static Dictionary<string, string> BuildRepoSqlFileIndex(string repoRoot)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in Directory.EnumerateFiles(repoRoot, "*.sql", SearchOption.AllDirectories))
                {
                    if (file.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        file.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        file.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(file);
                    if (!map.ContainsKey(fileName))
                    {
                        map[fileName] = file;
                    }

                    string rel = file.Substring(repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                    string[] segments = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    // If SSDT layout: "{schema}/Stored Procedures/{objName}.sql" or "{db}/{schema}/Stored Procedures/{objName}.sql"
                    if (segments.Length >= 3)
                    {
                        string parentSchemaCandidate = segments[segments.Length - 3];
                        string schemaObjKey = $"{parentSchemaCandidate}/{fileName}";
                        if (!map.ContainsKey(schemaObjKey))
                        {
                            map[schemaObjKey] = file;
                        }
                    }

                    if (segments.Length >= 2)
                    {
                        string firstFolder = segments[0];
                        string dbKey = $"{firstFolder}/{fileName}";
                        if (!map.ContainsKey(dbKey))
                        {
                            map[dbKey] = file;
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public async Task<List<GitCommitEntry>> GetFileHistoryAsync(
            ResolvedSqlObject resolvedObj,
            string branchOrRef = "HEAD",
            int? gitStartLine1Based = null,
            int? gitEndLine1Based = null,
            CancellationToken token = default)
        {
            var commits = new List<GitCommitEntry>();
            if (resolvedObj == null ||
                string.IsNullOrEmpty(resolvedObj.RepoRoot) ||
                !File.Exists(resolvedObj.GitFilePath))
            {
                return commits;
            }

            bool isLineRangeQuery = gitStartLine1Based.HasValue && gitEndLine1Based.HasValue;

            if (!isLineRangeQuery && !string.Equals(
                NormalizeLineEndings(resolvedObj.DiskGitSql),
                NormalizeLineEndings(resolvedObj.NormalizedLiveSql),
                StringComparison.Ordinal))
            {
                commits.Add(new GitCommitEntry
                {
                    RepoRoot = resolvedObj.RepoRoot,
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

            int maxCommits = Math.Max(10, GeneralOptions.Instance.MaxHistoryCommits);
            if (isLineRangeQuery)
            {
                int totalDiskLines = Math.Max(1, CountNewLines(resolvedObj.DiskGitSql) + 1);
                int sLine = Math.Max(1, Math.Min(gitStartLine1Based.Value, totalDiskLines));
                int eLine = Math.Max(sLine, Math.Min(gitEndLine1Based.Value, totalDiskLines));
                args = $"log {refArg} --no-patch -n {maxCommits} --pretty=format:\"%H%x1F%P%x1F%an%x1F%at%x1F%s\" -L {sLine},{eLine}:\"{resolvedObj.RelativeGitPath}\"";
            }
            else
            {
                args = $"log {refArg} --follow -n {maxCommits} --pretty=format:\"%H%x1F%P%x1F%an%x1F%at%x1F%s\" -- \"{resolvedObj.RelativeGitPath}\"";
            }

            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = resolvedObj.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Prevent OS stderr buffer deadlock
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            using (token.Register(() => SafeKillProcess(proc)))
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (token.IsCancellationRequested)
                    {
                        SafeKillProcess(proc);
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
                        RepoRoot = resolvedObj.RepoRoot,
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
            }

            return commits;
        }

        public async Task<(string LeftSql, string RightSql, string ParentShortSha, string CommitShortSha)> GetCommitDiffAsync(
                    GitCommitEntry entry)
        {
            if (entry == null)
                return ("", "", "", "");

            string repoRoot = !string.IsNullOrEmpty(entry.RepoRoot)
                ? entry.RepoRoot
                : GetEffectiveRepoRoot(null, entry.GitFilePath);

            if (string.IsNullOrEmpty(repoRoot))
                return ("", "", "", "");

            if (entry.IsWorkingTreeDiff)
            {
                string headSql = await RunGitShowAsync(repoRoot, $"HEAD:{entry.RelativeGitPath}").ConfigureAwait(false);
                return (headSql, entry.LiveBufferSql ?? "", "HEAD", "Live Editor");
            }

            Task<string> commitSqlTask = RunGitShowAsync(repoRoot, $"{entry.CommitSha}:{entry.RelativeGitPath}");
            Task<string> parentSqlTask = !string.IsNullOrEmpty(entry.ParentSha)
                ? RunGitShowAsync(repoRoot, $"{entry.ParentSha}:{entry.RelativeGitPath}")
                : Task.FromResult("");

            await Task.WhenAll(commitSqlTask, parentSqlTask).ConfigureAwait(false);

            string parentSql = await parentSqlTask.ConfigureAwait(false);
            string commitSql = await commitSqlTask.ConfigureAwait(false);

            return (
                parentSql ?? "",
                commitSql ?? "",
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
            CancellationToken token,
            string activeDocumentPath = null)
        {
            var result = new Dictionary<int, LineBlameViewModel>();
            if (string.IsNullOrWhiteSpace(sqlText) || sqlText.Length <= 20)
                return result;

            ParsedSqlObjectHeader parsedEditor = SqlScriptDomParser.ParseHeader(sqlText);
            if (parsedEditor == null || token.IsCancellationRequested) return result;

            string dbName = parsedEditor.DatabaseName;
            string repoRoot = GetEffectiveRepoRoot(dbName, activeDocumentPath);
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return result;

            string gitFilePath = FindSqlObjectFileInRepo(repoRoot, parsedEditor);
            if (string.IsNullOrEmpty(gitFilePath) || !File.Exists(gitFilePath))
                return result;

            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(gitFilePath);
            if (!string.Equals(_cachedGitFilePath, gitFilePath, StringComparison.OrdinalIgnoreCase) ||
                _cachedGitFileWriteTimeUtc != lastWriteUtc)
            {
                string gitFileText = File.ReadAllText(gitFilePath);
                _cachedGitFullText = gitFileText;
                ParsedSqlObjectHeader parsedGit = SqlScriptDomParser.ParseHeader(gitFileText);
                _cachedGitFilePath = gitFilePath;
                _cachedGitFileWriteTimeUtc = lastWriteUtc;

                if (parsedGit != null)
                {
                    _cachedGitHeaderPrefix = gitFileText.Substring(0, parsedGit.StatementStartOffset);
                    _cachedGitStartOffsetLine = parsedGit.StatementStartLine0Based;
                    _cachedGitKeyword = parsedGit.DdlVerbText;
                }
                else
                {
                    _cachedGitHeaderPrefix = "";
                    _cachedGitStartOffsetLine = 0;
                    _cachedGitKeyword = "";
                }
            }

            int spStartOffsetLine = parsedEditor.StatementStartLine0Based;
            string liveSpBody = sqlText.Substring(parsedEditor.StatementStartOffset);

            if (!string.IsNullOrEmpty(_cachedGitKeyword) &&
                !_cachedGitKeyword.Equals(parsedEditor.DdlVerbText, StringComparison.OrdinalIgnoreCase))
            {
                liveSpBody = _cachedGitKeyword + liveSpBody.Substring(parsedEditor.DdlVerbLength);
            }

            string syntheticContents = _cachedGitHeaderPrefix + liveSpBody;

            // -w ignores whitespace differences (CRLF/LF/indentation) between DB OBJECT_DEFINITION and Git file
            string whitespaceFlag = GeneralOptions.Instance.IgnoreWhitespaceInBlame ? "-w " : "";
            var psi = new ProcessStartInfo("git", $"blame --porcelain {whitespaceFlag}--contents - \"{gitFilePath}\"")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            using (token.Register(() => SafeKillProcess(proc)))
            {
                Task writeTask = Task.Run(async () =>
                {
                    try
                    {
                        using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
                        {
                            await stdin.WriteAsync(syntheticContents).ConfigureAwait(false);
                        }
                    }
                    catch { }
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
                        SafeKillProcess(proc);
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
                            DateTime dt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
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
                                    Date = "Working Tree",
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
                                    DisplayText = $"    -- {currentCommit.Date}   {currentCommit.Author}   {currentCommit.Summary}",
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
            }

            return result;
        }

        public async Task<(string LeftSql, string RightSql, string LeftLabel, string RightLabel)> GetDiffContentForTagAsync(
            LineBlameViewModel vm)
        {
            if (vm.IsUncommitted || string.IsNullOrEmpty(vm.CommitSha) || vm.CommitSha.StartsWith("00000000", StringComparison.Ordinal) || vm.CommitSha == "UNCOMMITTED")
            {
                return (vm.BaselineGitSql, vm.CurrentLiveSql, "Git Repo File (HEAD)", "Live SSMS Buffer (Uncommitted)");
            }

            string repoRoot = GetEffectiveRepoRoot(null, vm.GitFilePath);
            if (string.IsNullOrEmpty(repoRoot))
            {
                return (vm.BaselineGitSql, vm.CurrentLiveSql, "Git Repo File", "Live SSMS Buffer");
            }

            string relativePath = vm.GitFilePath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                ? vm.GitFilePath.Substring(repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/')
                : vm.GitFilePath;

            string shortSha = vm.CommitSha.Substring(0, 8);

            Task<string> parentSqlTask = RunGitShowAsync(repoRoot, $"{vm.CommitSha}~1:{relativePath}");
            Task<string> commitSqlTask = RunGitShowAsync(repoRoot, $"{vm.CommitSha}:{relativePath}");

            await Task.WhenAll(parentSqlTask, commitSqlTask).ConfigureAwait(false);

            string parentSql = await parentSqlTask.ConfigureAwait(false);
            string commitSql = await commitSqlTask.ConfigureAwait(false);

            if (string.IsNullOrEmpty(commitSql))
            {
                return (vm.BaselineGitSql, vm.CurrentLiveSql, "Git Repo File", "Live SSMS Buffer");
            }

            return (parentSql ?? "", commitSql, $"{shortSha}~1 (Before)", $"{shortSha} ({vm.Author}   {vm.Date})");
        }

        private static async Task<string> RunGitShowAsync(string repoRoot, string objectSpec)
        {
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return "";

            var psi = new ProcessStartInfo("git", $"show \"{objectSpec}\"")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Avoid stderr pipe deadlock
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            {
                string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                proc.WaitForExit(1000);
                return proc.ExitCode == 0 ? output : "";
            }
        }

        private static void SafeKillProcess(Process proc)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch { }
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