// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
internal static class DialogChrome
{
    internal static void Apply(ContentControl? title, Grid? commands, ColumnDefinition? column, Action resized,
        double commandsBottom, bool twoRows, Button? primary, Button? close, bool pinButtons, bool primaryMargin)
    {
        if (title is not null)
        {
            title.HorizontalAlignment = HorizontalAlignment.Stretch;
            title.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            title.SizeChanged += (_, _) => resized();
        }
        if (commands is not null)
        {
            commands.Padding = new(24, 0, 24, commandsBottom);
            if (twoRows)
            {
                commands.RowDefinitions.Add(new() { Height = GridLength.Auto });
                commands.RowDefinitions.Add(new() { Height = GridLength.Auto });
            }
            commands.SizeChanged += (_, _) => resized();
        }
        if (column is not null) column.Width = new(0);
        if (pinButtons)
        {
            StyleButton(primary, 0, primaryMargin);
            StyleButton(close, 1, false);
        }
    }
    private static void StyleButton(Button? button, int row, bool margin)
    {
        if (button is null) return;
        Grid.SetColumn(button, 0);
        Grid.SetColumnSpan(button, 5);
        Grid.SetRow(button, row);
        button.MinHeight = 48;
        button.Padding = new(16, 12, 16, 12);
        button.FontSize = 16;
        button.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        if (margin) button.Margin = new(0, 0, 0, 4);
    }
    internal static void Resize(FrameworkElement scroll, double rootWidth, double rootHeight,
        FrameworkElement? heading, FrameworkElement? commands, double maxWidth, double defaultHeading, double defaultAction, double pad)
    {
        scroll.Width = Math.Max(0, Math.Min(maxWidth, rootWidth - 96));
        var headingHeight = heading is null ? defaultHeading : heading.ActualHeight + heading.Margin.Top + heading.Margin.Bottom;
        var actionHeight = commands?.ActualHeight ?? defaultAction;
        scroll.MaxHeight = Math.Max(0, rootHeight - headingHeight - actionHeight - pad);
    }
}
