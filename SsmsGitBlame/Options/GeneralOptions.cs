using Community.VisualStudio.Toolkit;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SsmsGitBlame
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralOptionsPage : BaseOptionPage<GeneralOptions> { }
    }

    public enum SearchResultOpenMode
    {
        [Description("Open Live Database ALTER Script (Fallback to Git File)")]
        LiveDatabaseAlterScript,

        [Description("Open Local Git .sql File Directly")]
        LocalGitFile
    }

    public class GeneralOptions : BaseOptionModel<GeneralOptions>
    {
        // ================= 1. INLINE GIT BLAME =================

        [Category("1. Inline Git Blame")]
        [DisplayName("Enable Inline Blame by Default")]
        [Description("Automatically show inline Git Blame annotations when opening a SQL query window without needing to press Ctrl+Alt+B.")]
        [DefaultValue(false)]
        public bool EnableBlameByDefault { get; set; } = false;

        [Category("1. Inline Git Blame")]
        [DisplayName("Ignore Whitespace in Git Blame (-w)")]
        [Description("Pass -w to 'git blame' so indentation or CRLF/LF differences between SQL Server and Git don't mark lines as uncommitted.")]
        [DefaultValue(true)]
        public bool IgnoreWhitespaceInBlame { get; set; } = true;

        // ================= 2. GIT SEARCH =================

        [Category("2. Git Search")]
        [DisplayName("Open Search Result Action")]
        [Description("Choose whether double-clicking a result in Git Search fetches the live Stored Procedure from SQL Server as an ALTER script or opens the local .sql file from disk.")]
        [DefaultValue(SearchResultOpenMode.LiveDatabaseAlterScript)]
        public SearchResultOpenMode SearchOpenBehavior { get; set; } = SearchResultOpenMode.LiveDatabaseAlterScript;

        [Category("2. Git Search")]
        [DisplayName("Maximum Search Results")]
        [Description("Maximum number of matches returned by 'git grep' before truncating to keep the UI responsive.")]
        [DefaultValue(1500)]
        public int MaxSearchResults { get; set; } = 1500;

        // ================= 3. GIT HISTORY =================

        [Category("3. Git History")]
        [DisplayName("Default Branch / Ref")]
        [Description("Default Git branch or ref loaded in the Git History tool window (e.g., HEAD, main, master).")]
        [DefaultValue("HEAD")]
        public string DefaultBranch { get; set; } = "HEAD";

        [Category("3. Git History")]
        [DisplayName("Max History Commits")]
        [Description("Maximum number of commits to load in Git History (git log -n).")]
        [DefaultValue(250)]
        public int MaxHistoryCommits { get; set; } = 250;
    }
}