using Microsoft.VisualStudio.PlatformUI;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SsmsGitBlame
{
    public class SearchHighlightBlock : TextBlock
    {
        private static SolidColorBrush _normalForegroundBrush;
        private static SolidColorBrush _highlightBackgroundBrush;
        private static SolidColorBrush _highlightForegroundBrush;

        static SearchHighlightBlock()
        {
            UpdateThemeBrushes();
            VSColorTheme.ThemeChanged += _ => UpdateThemeBrushes();
        }

        private static void UpdateThemeBrushes()
        {
            var textDrawingColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            bool isDarkTheme = textDrawingColor.GetBrightness() > 0.5f;

            var normalBrush = new SolidColorBrush(Color.FromArgb(
                textDrawingColor.A, textDrawingColor.R, textDrawingColor.G, textDrawingColor.B));
            normalBrush.Freeze();
            _normalForegroundBrush = normalBrush;

            var hlBg = isDarkTheme
                ? new SolidColorBrush(Color.FromRgb(115, 85, 20))
                : new SolidColorBrush(Color.FromRgb(255, 231, 146));
            hlBg.Freeze();
            _highlightBackgroundBrush = hlBg;

            var hlFg = isDarkTheme
                ? new SolidColorBrush(Color.FromRgb(255, 245, 210))
                : new SolidColorBrush(Color.FromRgb(30, 31, 34));
            hlFg.Freeze();
            _highlightForegroundBrush = hlFg;
        }

        public static readonly DependencyProperty ItemProperty =
            DependencyProperty.Register(
                nameof(Item),
                typeof(SearchResultItem),
                typeof(SearchHighlightBlock),
                new PropertyMetadata(null, OnItemChanged));

        public SearchResultItem Item
        {
            get => (SearchResultItem)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not SearchHighlightBlock block) return;

            block.Inlines.Clear();
            if (e.NewValue is not SearchResultItem item) return;

            string text = item.LineContent ?? "";
            if (text.Length == 0) return;

            Regex regex = item.HighlightRegex;
            if (regex == null)
            {
                block.Inlines.Add(new Run(text) { Foreground = _normalForegroundBrush });
                return;
            }

            int lastIndex = 0;
            try
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (match.Length == 0) continue;

                    if (match.Index > lastIndex)
                    {
                        block.Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex))
                        {
                            Foreground = _normalForegroundBrush
                        });
                    }

                    block.Inlines.Add(new Run(match.Value)
                    {
                        Background = _highlightBackgroundBrush,
                        Foreground = _highlightForegroundBrush,
                        FontWeight = FontWeights.SemiBold
                    });

                    lastIndex = match.Index + match.Length;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                block.Inlines.Clear();
                block.Inlines.Add(new Run(text) { Foreground = _normalForegroundBrush });
                return;
            }

            if (lastIndex < text.Length)
            {
                block.Inlines.Add(new Run(text.Substring(lastIndex))
                {
                    Foreground = _normalForegroundBrush
                });
            }
        }
    }
}