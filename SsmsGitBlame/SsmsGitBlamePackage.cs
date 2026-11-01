using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsGitBlame
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralOptionsPage), "SSMS Git Blame", "General", 0, 0, true, SupportsProfiles = true)]
    [ProvideProfile(typeof(OptionsProvider.GeneralOptionsPage), "SSMS Git Blame", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(GitHistoryToolWindow.Pane),
        Style = VsDockStyle.Tabbed,
        Window = WindowGuids.OutputWindow,
        Orientation = ToolWindowOrientation.Bottom)]
    [ProvideToolWindow(typeof(GitSearchToolWindow.Pane),
        Style = VsDockStyle.Float,
        Window = WindowGuids.OutputWindow)]
    [ProvideToolWindow(typeof(GitCompareMergeToolWindow.Pane),
        Style = VsDockStyle.MDI)]
    [Guid(PackageGuids.SsmsGitBlameString)]
    public sealed class SsmsGitBlamePackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}