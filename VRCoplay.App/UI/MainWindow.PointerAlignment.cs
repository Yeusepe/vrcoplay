// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private bool _updatingPointerAlignment;
    private void AutoAlignPointer_Click(object sender, RoutedEventArgs e) { _session.Input?.AlignPointer(); UpdatePointerAlignment(); }
    private void SkipPointerAlignment_Click(object sender, RoutedEventArgs e) { _session.Input?.SkipPointerAlignment(); UpdatePointerAlignment(); }
    private void ResetPointerAlignment_Click(object sender, RoutedEventArgs e) { _session.Input?.ResetPointerAlignment(); UpdatePointerAlignment(); }
    private void PointerAim_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPointerAlignment || PointerAimXSlider is null || PointerAimYSlider is null) return;
        _session.Input?.TrimPointer((float)PointerAimXSlider.Value, (float)PointerAimYSlider.Value);
    }
    private void UpdatePointerAlignment()
    {
        if (PointerAlignmentPanel is null) return;
        var alignment = _session.Input?.PointerAlignment ?? default;
        PointerAlignmentPanel.Visibility = alignment.Available ? Visibility.Visible : Visibility.Collapsed;
        if (!alignment.Available) return;
        PointerAlignmentText.Text = string.IsNullOrEmpty(alignment.Message) ? "Use the sliders to adjust your aim." : alignment.Message;
        AutoAlignPointerButton.Content = alignment.Phase == 2 ? "Aligning…" : alignment.Phase == 5 ? "Try again" : "Align controller";
        AutoAlignPointerButton.IsEnabled = alignment.Phase != 2;
        SkipPointerAlignmentButton.IsEnabled = alignment.Phase != 0;
        _updatingPointerAlignment = true;
        try { PointerAimXSlider.Value = alignment.X; PointerAimYSlider.Value = alignment.Y; }
        finally { _updatingPointerAlignment = false; }
    }
}
