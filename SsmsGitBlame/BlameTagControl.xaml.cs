using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SsmsGitBlame
{
    public partial class BlameTagControl : UserControl
    {
        public event EventHandler<LineBlameViewModel> TagClicked;

        public BlameTagControl()
        {
            InitializeComponent();
        }

        private void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is LineBlameViewModel vm)
            {
                e.Handled = true;
                TagClicked?.Invoke(this, vm);
            }
        }
    }

    public sealed class LineBlameViewModel
    {
        public string DisplayText { get; set; }
        public bool IsUncommitted { get; set; }
        public FontFamily EditorFontFamily { get; set; } = new FontFamily("Consolas");
        public double EditorFontSize { get; set; } = 12.0;
        public string CommitSha { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string Summary { get; set; }

        public int EditorLineNumber { get; set; }
        public int GitLineNumber { get; set; }
        public string GitFilePath { get; set; }
        public string BaselineGitSql { get; set; }
        public string CurrentLiveSql { get; set; }
    }
}