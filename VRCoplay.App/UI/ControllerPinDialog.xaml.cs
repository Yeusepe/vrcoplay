// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
namespace VRCoplay;
public sealed partial class ControllerPinDialog : ContentDialog
{
    private readonly bool _masked;
    private readonly Border[] _focus;
    private readonly TextBlock[] _characters;
    internal string Pin => PinBox.Text;
    internal ControllerPinDialog(bool masked)
    {
        _masked = masked;
        InitializeComponent();
        _focus = [PinFocus0, PinFocus1, PinFocus2, PinFocus3, PinFocus4, PinFocus5];
        _characters = [PinCharacter0, PinCharacter1, PinCharacter2, PinCharacter3, PinCharacter4, PinCharacter5];
        Opened += (_, _) => PinBox.Focus(FocusState.Programmatic);
    }
    private void Pin_Changed(object sender, TextChangedEventArgs e)
    {
        var pin = string.Concat(PinBox.Text.Where(char.IsAsciiDigit));
        if (pin != PinBox.Text)
        {
            PinBox.Text = pin;
            PinBox.SelectionStart = pin.Length;
            return;
        }
        PinFeedback.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = pin.Length == 6;
        UpdatePresentation();
    }
    private void Pin_SelectionChanged(object sender, RoutedEventArgs e) => UpdatePresentation();
    private void Pin_FocusChanged(object sender, RoutedEventArgs e) => UpdatePresentation();
    private void UpdatePresentation()
    {
        if (_characters is null) return;
        var pin = PinBox.Text;
        var ready = pin.Length == 6;
        for (var index = 0; index < _characters.Length; index++)
        {
            _characters[index].Text = index < pin.Length ? _masked ? "•" : pin[index].ToString() : "";
            _focus[index].Visibility = ready || PinBox.FocusState != FocusState.Unfocused
                && index == Math.Min(PinBox.SelectionStart, pin.Length) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private async void PastePin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                PinFeedback.Visibility = Visibility.Visible;
                return;
            }
            var pin = string.Concat((await content.GetTextAsync()).Where(char.IsAsciiDigit));
            if (pin.Length != 6)
            {
                PinFeedback.Visibility = Visibility.Visible;
                return;
            }
            PinBox.Text = pin;
            PinBox.SelectionStart = pin.Length;
            PinBox.Focus(FocusState.Programmatic);
        }
        catch
        {
            PinFeedback.Visibility = Visibility.Visible;
        }
    }
}
