using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SsmsGitBlame.Services;

namespace SsmsGitBlame
{
    public class SearchHighlightBlock : TextBlock
    {
        public static readonly DependencyProperty ItemProperty =
            DependencyProperty.Register(nameof(Item), typeof(SearchResultItem), typeof(SearchHighlightBlock),
                new PropertyMetadata(null, OnItemChanged));

        public SearchResultItem Item
        {
            get => (SearchResultItem)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SearchHighlightBlock block && e.NewValue is SearchResultItem item)
            {
                block.Inlines.Clear();
                string text = item.LineContent ?? "";
                string query = item.SearchPattern ?? "";

                if (string.IsNullOrEmpty(query))
                {
                    block.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(Color.FromRgb(39, 40, 44)) });
                    return;
                }

                var regexOptions = item.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                string pattern = item.UseRegex ? query : Regex.Escape(query);

                int lastIndex = 0;
                try
                {
                    foreach (Match match in Regex.Matches(text, pattern, regexOptions))
                    {
                        if (match.Index > lastIndex)
                        {
                            block.Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex))
                            {
                                Foreground = new SolidColorBrush(Color.FromRgb(39, 40, 44))
                            });
                        }

                        // JetBrains light theme yellow highlight
                        var highlight = new Run(match.Value)
                        {
                            Background = new SolidColorBrush(Color.FromRgb(255, 231, 146)),
                            Foreground = new SolidColorBrush(Color.FromRgb(39, 40, 44)),
                            FontWeight = FontWeights.SemiBold
                        };
                        block.Inlines.Add(highlight);
                        lastIndex = match.Index + match.Length;
                    }
                }
                catch
                {
                    block.Inlines.Clear();
                    block.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(Color.FromRgb(39, 40, 44)) });
                    return;
                }

                if (lastIndex < text.Length)
                {
                    block.Inlines.Add(new Run(text.Substring(lastIndex))
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(39, 40, 44))
                    });
                }
            }
        }
    }
}