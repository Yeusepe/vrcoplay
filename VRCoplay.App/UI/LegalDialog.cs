// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
namespace VRCoplay;
internal sealed class LegalDialog : ContentDialog
{
    private readonly ScrollViewer _scroll;
    private readonly MarkdownTextBlock _markdown;
    internal LegalDialog(string directory)
    {
        Title = "Copyright and licenses";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        FontFamily = (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"];
        Resources["ContentDialogMaxWidth"] = 720d;
        Content = _scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _markdown = new MarkdownTextBlock
            {
                UsePipeTables = true,
                UseAutoLinks = true,
                DisableHtml = true,
                IsTextSelectionEnabled = true,
                FontSize = 14,
                Text = ReadDocument(directory),
            },
        };
        Opened += (_, _) => { ApplyMarkdownTheme(); ResizeBody(); XamlRoot.Changed += RootChanged; };
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyMarkdownTheme);
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }
    private void ApplyMarkdownTheme()
    {
        if (!_markdown.IsLoaded) return;
        _markdown.Config = new MarkdownConfig
        {
            Themes = new MarkdownThemes
            {
                H1Foreground = Foreground, H2Foreground = Foreground, H3Foreground = Foreground,
                H4Foreground = Foreground, H5Foreground = Foreground, H6Foreground = Foreground,
                InlineCodeForeground = Foreground, CodeBlockForeground = Foreground, QuoteForeground = Foreground,
                InlineCodeBackground = Background, CodeBlockBackground = Background,
                TableHeadingBackground = Background, LinkForeground = Foreground, InlineCodeFontSize = 14,
            },
        };
        var text = _markdown.Text;
        _markdown.Text = "";
        _markdown.Text = text;
        _markdown.UpdateLayout();
        foreach (var block in _markdown.FindDescendants().OfType<RichTextBlock>().ToArray())
            foreach (var paragraph in block.Blocks.OfType<Paragraph>())
                WrapInlineCode(paragraph.Inlines);
    }
    private static void WrapInlineCode(InlineCollection inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
        {
            if (inlines[i] is Span span) WrapInlineCode(span.Inlines);
            else if (inlines[i] is InlineUIContainer { Child: Border { Child: TextBlock code } })
            {
                var run = new Run { Text = code.Text, FontFamily = new FontFamily("Consolas") };
                inlines.RemoveAt(i);
                inlines.Insert(i, run);
            }
        }
    }
    private static string ReadDocument(string directory)
    {
        var files = new[] { ("COPYRIGHT.txt", "Copyright"), ("VRCoplay-LICENSE.txt", "VRCoplay license"),
            ("THIRD-PARTY-NOTICES.md", "Third-party notices") };
        return string.Join("\n\n", files.Select(file =>
        {
            var path = System.IO.Path.Combine(directory, file.Item1);
            if (!System.IO.File.Exists(path))
                return $"# {file.Item2}\n\n{file.Item1} is missing from this installation.";
            var text = System.IO.File.ReadAllText(path);
            if (file.Item1.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return text;
            return $"# {file.Item2}\n\n" + string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()));
        }));
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
    private void ResizeBody() => DialogChrome.Resize(_scroll, XamlRoot.Size.Width, XamlRoot.Size.Height,
        GetTemplateChild("Title") as FrameworkElement, GetTemplateChild("CommandSpace") as FrameworkElement,
        652, 56, 80, 80);
}
