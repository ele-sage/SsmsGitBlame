using Community.VisualStudio.Toolkit;
using EnvDTE;
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using DialogResult = System.Windows.Forms.DialogResult;
using envDTE80 = EnvDTE80;

namespace SsmsGitBlame
{
    public static class GitRepositoryConfigService
    {
        private static readonly string GlobalConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SsmsGitBlame",
            "repositories.conf");

        public static event EventHandler RepositoryChanged;

        /// <summary>
        /// Resolves the best Git repository root for the current context:
        /// 1. Per-Solution setting ({SolutionDir}\.vs\SsmsGitBlame.conf) or Solution's own Git root
        /// 2. Active Document's own Git root (if saved on disk inside a repo)
        /// 3. Per-Database mapping (e.g. d1_legos -> path) in %AppData%\SsmsGitBlame\repositories.conf
        /// 4. User Default repo in %AppData%\SsmsGitBlame\repositories.conf
        /// </summary>
        public static string ResolveRepoRoot(string dbName = null, string activeDocumentFilePath = null)
        {
            // 1. Check active SSMS Solution (.ssmssln)
            string solutionDir = GetActiveSolutionDirectory();
            Debug.WriteLine($"Solution directory: {solutionDir}");
            if (!string.IsNullOrEmpty(solutionDir))
            {
                // 1a. Check if user saved an explicit repo override for this Solution
                string slnConfigFile = GetSolutionConfigFilePath(solutionDir);
                var slnSettings = ReadKeyValueFile(slnConfigFile);

                if (!string.IsNullOrEmpty(dbName) &&
                    slnSettings.TryGetValue($"db:{dbName}", out string slnDbRepo) &&
                    Directory.Exists(slnDbRepo))
                {
                    Debug.WriteLine($"Using per-database repo for [{dbName}] from Solution config: {slnDbRepo}");
                    return slnDbRepo;
                }

                if (slnSettings.TryGetValue("default", out string slnDefaultRepo) &&
                    Directory.Exists(slnDefaultRepo))
                {
                    Debug.WriteLine($"Using default repo from Solution config: {slnDefaultRepo}");
                    return slnDefaultRepo;
                }

                // 1b. Check if the Solution file itself sits inside a Git repository
                string slnGitRoot = FindGitRootUpwards(solutionDir);
                if (!string.IsNullOrEmpty(slnGitRoot))
                {
                    Debug.WriteLine($"Using Git root from Solution directory: {slnGitRoot}");
                    return slnGitRoot;
                }
            }

            // 2. Check if the active .sql file is saved inside a Git repository
            if (!string.IsNullOrEmpty(activeDocumentFilePath))
            {
                string docGitRoot = FindGitRootUpwards(Path.GetDirectoryName(activeDocumentFilePath));
                if (!string.IsNullOrEmpty(docGitRoot))
                {
                    Debug.WriteLine($"Using Git root from active document: {docGitRoot}");
                    return docGitRoot;
                }
            }

            // 3. Check Global / Per-Database config in %AppData%\SsmsGitBlame\repositories.conf
            var globalSettings = ReadKeyValueFile(GlobalConfigFilePath);
            if (!string.IsNullOrEmpty(dbName) &&
                globalSettings.TryGetValue($"db:{dbName}", out string dbRepo) &&
                Directory.Exists(dbRepo))
            {
                Debug.WriteLine($"Using per-database repo for [{dbName}] from global config: {dbRepo}");
                return dbRepo;
            }

            if (globalSettings.TryGetValue("default", out string defaultRepo) &&
                Directory.Exists(defaultRepo))
            {
                Debug.WriteLine($"Using default repo from global config: {defaultRepo}");
                return defaultRepo;
            }

            return null;
        }

        /// <summary>
        /// Opens a Folder Browser dialog allowing the user to pick the Git repository root.
        /// Saves it to the active SSMS Solution (if one is open) AND as the default/database mapping.
        /// </summary>
        public static string PromptUserForRepository(string currentDbName = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string currentRepo = ResolveRepoRoot(currentDbName);
            string solutionDir = GetActiveSolutionDirectory();

            using (var dialog = new FolderBrowserDialog())
            {
                string scopeLabel = !string.IsNullOrEmpty(solutionDir)
                    ? $"current Solution ({Path.GetFileName(solutionDir)})"
                    : (!string.IsNullOrEmpty(currentDbName) ? $"database [{currentDbName}]" : "SSMS Git History");

                dialog.Description = $"Select the root Git repository folder for {scopeLabel}:";
                dialog.ShowNewFolderButton = false;

                if (!string.IsNullOrEmpty(currentRepo) && Directory.Exists(currentRepo))
                {
                    dialog.SelectedPath = currentRepo;
                }

                if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    string selectedPath = dialog.SelectedPath.Trim();

                    // If they picked a subfolder inside a Git repo, normalize to the top-level .git root
                    string gitRoot = FindGitRootUpwards(selectedPath) ?? selectedPath;

                    SaveRepositorySetting(gitRoot, currentDbName, solutionDir);
                    RepositoryChanged?.Invoke(null, EventArgs.Empty);
                    return gitRoot;
                }
            }

            return null;
        }

        public static void SaveRepositorySetting(string repoRoot, string dbName = null, string solutionDir = null)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            // 1. If a Solution is currently open in SSMS, save it to {SolutionDir}\.vs\SsmsGitBlame.conf
            if (!string.IsNullOrEmpty(solutionDir))
            {
                string slnConfigPath = GetSolutionConfigFilePath(solutionDir);
                var slnSettings = ReadKeyValueFile(slnConfigPath);
                slnSettings["default"] = repoRoot;
                if (!string.IsNullOrEmpty(dbName))
                {
                    slnSettings[$"db:{dbName}"] = repoRoot;
                }
                WriteKeyValueFile(slnConfigPath, slnSettings);
            }

            // 2. Also save to %AppData%\SsmsGitBlame\repositories.conf for ad-hoc query windows without a solution
            var globalSettings = ReadKeyValueFile(GlobalConfigFilePath);
            globalSettings["default"] = repoRoot;
            if (!string.IsNullOrEmpty(dbName))
            {
                globalSettings[$"db:{dbName}"] = repoRoot;
            }
            WriteKeyValueFile(GlobalConfigFilePath, globalSettings);
        }

        public static string GetActiveSolutionDirectory()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Package.GetGlobalService(typeof(DTE)) is DTE dte &&
                    dte.Solution != null &&
                    !string.IsNullOrWhiteSpace(dte.Solution.FullName))
                {
                    return Path.GetDirectoryName(dte.Solution.FullName);
                }
            }
            catch
            {
                // Ignore if called from background thread or DTE unavailable
            }
            return null;
        }

        private static string GetSolutionConfigFilePath(string solutionDir)
        {
            return Path.Combine(solutionDir, ".vs", "SsmsGitBlame.conf");
        }

        private static string FindGitRootUpwards(string startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir)) return null;

            try
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null && dir.Exists)
                {
                    string gitPath = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch
            {
            }
            return null;
        }

        private static Dictionary<string, string> ReadKeyValueFile(string filePath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return dict;

            try
            {
                foreach (string rawLine in File.ReadAllLines(filePath))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string key = line.Substring(0, eqIdx).Trim();
                        string val = line.Substring(eqIdx + 1).Trim();
                        dict[key] = val;
                    }
                }
            }
            catch
            {
            }
            return dict;
        }

        private static void WriteKeyValueFile(string filePath, Dictionary<string, string> dict)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var lines = new List<string>
                {
                    "# SsmsGitBlame Repository Configuration",
                    $"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                };

                foreach (var kvp in dict)
                {
                    lines.Add($"{kvp.Key}={kvp.Value}");
                }

                File.WriteAllLines(filePath, lines);
            }
            catch
            {
            }
        }
    }
}