using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsGitBlame.Services
{
    public class SearchResultItem
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public int LineNumber { get; set; }
        public string LineContent { get; set; }
        public string SearchPattern { get; set; }
        public bool MatchCase { get; set; }
        public bool UseRegex { get; set; }
        public string FileName => System.IO.Path.GetFileName(RelativePath);
        public string DirectoryPath => System.IO.Path.GetDirectoryName(RelativePath);
    }

    public class GitSearchService
    {
        private static readonly Regex GitGrepRegex = new Regex(@"^(?<file>.+?):(?<line>\d+):(?<content>.*)$", RegexOptions.Compiled);

        public async Task<List<SearchResultItem>> SearchAsync(
            string repoRoot,
            string pattern,
            bool matchCase,
            bool matchWholeWord,
            bool useRegex,
            string filePattern = "*.sql",
            CancellationToken ct = default)
        {
            var results = new List<SearchResultItem>();
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(pattern))
                return results;

            // Build git grep arguments
            // -I ignores binary files, -n outputs line numbers
            string args = "-c color.grep=false grep -n -I";

            if (!matchCase) args += " -i";
            if (matchWholeWord) args += " -w";
            if (useRegex) args += " -E"; // Extended regex
            else args += " -F";          // Fixed string match (literal)

            string escapedPattern = pattern.Replace("\"", "\\\"");
            args += $" -e \"{escapedPattern}\"";

            if (!string.IsNullOrWhiteSpace(filePattern) && filePattern != "*")
            {
                args += $" -- \"{filePattern}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();

                using (ct.Register(() => { try { process.Kill(); } catch { } }))
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line = await process.StandardOutput.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var match = GitGrepRegex.Match(line);
                        if (match.Success)
                        {
                            string relPath = match.Groups["file"].Value;
                            int lineNum = int.Parse(match.Groups["line"].Value);
                            string text = match.Groups["content"].Value;

                            results.Add(new SearchResultItem
                            {
                                RelativePath = relPath,
                                FullPath = Path.Combine(repoRoot, relPath),
                                LineNumber = lineNum,
                                LineContent = text,
                                SearchPattern = pattern,
                                MatchCase = matchCase,
                                UseRegex = useRegex
                            });
                        }
                    }
                    await Task.Run(() => process.WaitForExit(), ct);
                }
            }

            return results;
        }
    }
}