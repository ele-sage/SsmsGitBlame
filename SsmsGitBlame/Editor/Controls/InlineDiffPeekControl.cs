using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SsmsGitBlame
{
    internal sealed class InlineDiffPeekControl : Border
    {
        private readonly IWpfDifferenceViewer _diffViewer;
        private readonly IDifferenceBuffer _diffBuffer;
        private bool _isClosed;

        public event EventHandler CloseRequested;

        public InlineDiffPeekControl(
            string leftSql,
            string rightSql,
            string headerTitle,
            int targetLineNumber,
            double width)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Width = Math.Max(750, width);
            Height = 320;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(2);
            Margin = new Thickness(0, 4, 0, 4);

            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
            var diffBufferFactory = componentModel.GetService<IDifferenceBufferFactoryService>();
            var diffViewerFactory = componentModel.GetService<IWpfDifferenceViewerFactoryService>();
            var contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();

            IContentType sqlContentType = contentTypeRegistry.GetContentType("SQL Server Tools")
                                       ?? contentTypeRegistry.GetContentType("SQL")
                                       ?? contentTypeRegistry.GetContentType("text");

            ITextBuffer leftBuffer = bufferFactory.CreateTextBuffer(leftSql ?? "", sqlContentType);
            ITextBuffer rightBuffer = bufferFactory.CreateTextBuffer(rightSql ?? "", sqlContentType);

            _diffBuffer = diffBufferFactory.CreateDifferenceBuffer(leftBuffer, rightBuffer);
            _diffViewer = diffViewerFactory.CreateDifferenceView(_diffBuffer);
            _diffViewer.ViewMode = DifferenceViewMode.SideBySide;

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 0, 8, 0)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var prevBtn = CreateToolbarButton("↑ Prev Change", () => _diffViewer.ScrollToPreviousChange());
            var nextBtn = CreateToolbarButton("↓ Next Change", () => _diffViewer.ScrollToNextChange());
            leftStack.Children.Add(prevBtn);
            leftStack.Children.Add(nextBtn);
            Grid.SetColumn(leftStack, 0);

            var titleBlock = new TextBlock
            {
                Text = headerTitle,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleBlock, 1);

            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button modeBtn = null;
            modeBtn = CreateToolbarButton("⇄ Switch to Inline", () =>
            {
                if (_diffViewer.ViewMode == DifferenceViewMode.SideBySide)
                {
                    _diffViewer.ViewMode = DifferenceViewMode.Inline;
                    modeBtn.Content = "⇄ Switch to Side-by-Side";
                }
                else
                {
                    _diffViewer.ViewMode = DifferenceViewMode.SideBySide;
                    modeBtn.Content = "⇄ Switch to Inline";
                }
            });

            var closeBtn = CreateToolbarButton("✕ Close (Esc)", () => ClosePopUp());
            rightStack.Children.Add(modeBtn);
            rightStack.Children.Add(closeBtn);
            Grid.SetColumn(rightStack, 2);

            headerGrid.Children.Add(leftStack);
            headerGrid.Children.Add(titleBlock);
            headerGrid.Children.Add(rightStack);
            headerBorder.Child = headerGrid;

            Grid.SetRow(headerBorder, 0);
            Grid.SetRow(_diffViewer.VisualElement, 1);

            rootGrid.Children.Add(headerBorder);
            rootGrid.Children.Add(_diffViewer.VisualElement);
            Child = rootGrid;

            _diffBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    ClosePopUp();
                }
            };

            // Fixed VSTHRD001 & VSTHRD110: Use JoinableTaskFactory instead of Dispatcher.BeginInvoke
            void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
            {
                _diffBuffer.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_diffViewer.IsClosed) return;

                    IWpfTextView activeView = _diffViewer.ViewMode == DifferenceViewMode.Inline
                        ? _diffViewer.InlineView
                        : _diffViewer.RightView;

                    int safeLine = Math.Max(0, Math.Min(targetLineNumber, activeView.TextSnapshot.LineCount - 1));
                    ITextSnapshotLine line = activeView.TextSnapshot.GetLineFromLineNumber(safeLine);
                    activeView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(line.Start, line.End), EnsureSpanVisibleOptions.AlwaysCenter);
                }).FileAndForget(nameof(OnSnapshotDifferenceChanged));
            }
        }

        private static Button CreateToolbarButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                Background = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 90)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                FontSize = 11
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        public void ClosePopUp()
        {
            if (_isClosed) return;
            _isClosed = true;

            if (!_diffViewer.IsClosed)
            {
                _diffViewer.Close();
            }
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}