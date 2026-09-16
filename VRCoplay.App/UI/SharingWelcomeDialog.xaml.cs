// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
namespace VRCoplay;
public sealed partial class SharingWelcomeDialog : ContentDialog
{
    public SharingWelcomeDialog()
    {
        InitializeComponent();
        GettingFocus += FocusPrimaryAction;
        Opened += (_, _) =>
        {
            ResizeBody();
            XamlRoot.Changed += Root_Changed;
            WelcomeScroll.ChangeView(null, 0, null, disableAnimation: true);
        };
        Closed += (_, _) => XamlRoot.Changed -= Root_Changed;
    }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
    private void FocusPrimaryAction(UIElement sender, GettingFocusEventArgs args)
    {
        if (GetTemplateChild("PrimaryButton") is Button primary && args.TrySetNewFocusedElement(primary))
            GettingFocus -= FocusPrimaryAction;
    }
    private void SharingDetails_Click(object sender, RoutedEventArgs e) =>
        FlyoutBase.ShowAttachedFlyout(SharingDetailsButton);
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DialogChrome.Apply(
            GetTemplateChild("Title") as ContentControl,
            GetTemplateChild("CommandSpace") as Grid,
            GetTemplateChild("PrimaryColumn") as ColumnDefinition,
            ResizeBody,
            commandsBottom: 24,
            twoRows: false,
            primary: null,
            close: null,
            pinButtons: false,
            primaryMargin: false
        );
        if (GetTemplateChild("PrimaryButton") is Button primary)
        {
            primary.MinHeight = 48;
            primary.Padding = new(16, 12, 16, 12);
            primary.CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
            primary.FontSize = 16;
            primary.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        }
    }
    private void ResizeBody()
    {
        if (XamlRoot is null) return;
        DialogChrome.Resize(WelcomeScroll, XamlRoot.Size.Width, XamlRoot.Size.Height,
            GetTemplateChild("Title") as FrameworkElement, GetTemplateChild("CommandSpace") as FrameworkElement,
            352, 132, 68, 76);
    }
}
