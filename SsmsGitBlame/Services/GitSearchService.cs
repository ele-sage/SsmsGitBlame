using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    public sealed class SearchResultItem
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public int LineNumber { get; set; }
        public string LineContent { get; set; }
        public Regex HighlightRegex { get; set; }

        public string FileName => Path.GetFileName(RelativePath);
        public string DirectoryPath => Path.GetDirectoryName(RelativePath);
    }

    public sealed class GitSearchResponse
    {
        public List<SearchResultItem> Items { get; } = new List<SearchResultItem>();
        public bool IsTruncated { get; set; }
        public Regex HighlightRegex { get; set; }
    }

    public sealed class GitSearchService
    {
        public const int MaxResults = 1500;
        private const int MaxSnippetLength = 320;

        // Uses NonBacktracking/explicit structure for fast parsing of "path:line:content"
        private static readonly Regex GitGrepRegex = new Regex(
            @"^(?<file>[^\0]+?):(?<line>\d+):(?<content>.*)$",
            RegexOptions.Compiled);

        public static Regex BuildHighlightRegex(string pattern, bool matchCase, bool matchWholeWord, bool useRegex)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            try
            {
                string corePattern = useRegex ? pattern : Regex.Escape(pattern);
                if (matchWholeWord)
                {
                    corePattern = $@"\b(?:{corePattern})\b";
                }

                var options = RegexOptions.Compiled | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new Regex(corePattern, options, TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                return null;
            }
        }

        public async Task<GitSearchResponse> SearchAsync(
            string repoRoot,
            string pattern,
            bool matchCase,
            bool matchWholeWord,
            bool useRegex,
            string filePattern = "*.sql",
            CancellationToken ct = default)
        {
            var response = new GitSearchResponse();
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(pattern))
                return response;

            Regex highlightRegex = BuildHighlightRegex(pattern, matchCase, matchWholeWord, useRegex);
            response.HighlightRegex = highlightRegex;

            var argsBuilder = new StringBuilder("-c color.grep=false grep -n -I --no-color");
            if (!matchCase) argsBuilder.Append(" -i");
            if (matchWholeWord) argsBuilder.Append(" -w");
            argsBuilder.Append(useRegex ? " -E" : " -F");

            string escapedPattern = pattern.Replace("\"", "\\\"");
            argsBuilder.Append($" -e \"{escapedPattern}\"");

            if (!string.IsNullOrWhiteSpace(filePattern) && filePattern != "*")
            {
                // Support comma/semicolon-separated masks like "*.sql, *.prc"
                string[] masks = filePattern.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                argsBuilder.Append(" --");
                foreach (string mask in masks)
                {
                    string trimmed = mask.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        argsBuilder.Append($" \"{trimmed}\"");
                    }
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = argsBuilder.ToString(),
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Avoid stderr pipe buffer deadlock
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();

                using (ct.Register(() => SafeKillProcess(process)))
                {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        Match match = GitGrepRegex.Match(line);
                        if (!match.Success) continue;

                        if (response.Items.Count >= MaxResults)
                        {
                            response.IsTruncated = true;
                            SafeKillProcess(process);
                            break;
                        }

                        string relPath = match.Groups["file"].Value;
                        if (!int.TryParse(match.Groups["line"].Value, out int lineNum))
                            continue;

                        string rawContent = match.Groups["content"].Value.Trim();
                        if (rawContent.Length > MaxSnippetLength)
                        {
                            rawContent = rawContent.Substring(0, MaxSnippetLength) + "...";
                        }

                        response.Items.Add(new SearchResultItem
                        {
                            RelativePath = relPath,
                            FullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar)),
                            LineNumber = lineNum,
                            LineContent = rawContent,
                            HighlightRegex = highlightRegex
                        });
                    }
                }
            }

            return response;
        }

        private static void SafeKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Ignore if process already exited
            }
        }
    }
}