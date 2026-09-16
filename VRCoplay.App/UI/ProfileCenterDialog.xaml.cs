// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class ProfileCenterDialog : ContentDialog
{
    private ScrollViewer? _scroller;
    private readonly ProfileEntry[] _profiles = ProfileCatalog
        .All.OrderBy(profile => profile.Software, StringComparer.OrdinalIgnoreCase)
        .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
        .Select(profile => new ProfileEntry(profile))
        .ToArray();
    public ProfileCenterDialog()
    {
        InitializeComponent();
        SearchBox.RegisterPropertyChangedCallback(AutoSuggestBox.TextProperty, (_, _) => FilterProfiles());
        FilterProfiles();
        Loaded += (_, _) => ResizeBody();
        Opened += (_, _) =>
        {
            ResizeBody();
            XamlRoot.Changed += Root_Changed;
            ProfileList.ApplyTemplate();
            _scroller = ProfileList.FindDescendant<ScrollViewer>();
            Bindings.Update();
            SearchBox.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) =>
        {
            XamlRoot.Changed -= Root_Changed;
            Bindings.StopTracking();
            _scroller = null;
        };
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("CloseButton") is Button close)
            close.HorizontalAlignment = HorizontalAlignment.Right;
    }
    private double EdgeOpacity(double extent, double offset, Visibility visibility) =>
        visibility == Visibility.Visible ? Math.Clamp((extent - offset) / 32, 0, 1) : 0;
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
    private void ResizeBody()
    {
        DialogBody.Width = Math.Max(0, Math.Min(600, XamlRoot.Size.Width - 80));
        DialogBody.Height = Math.Max(80, Math.Min(480, XamlRoot.Size.Height - 200));
    }
    private void FilterProfiles()
    {
        var matches = _profiles.Where(entry => entry.Profile.Matches(SearchBox.Text)).ToArray();
        ProfileList.ItemsSource = matches;
        ResultCount.Text = matches.Length == 1 ? "1 profile package" : $"{matches.Length} profile packages";
        EmptyState.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileList.Visibility = matches.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (matches.Length > 0)
            ProfileList.ScrollIntoView(matches[0]);
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProfileEntry entry })
            await entry.InstallAsync();
    }
}
public sealed class ProfileEntry(ControllerProfile profile) : INotifyPropertyChanged
{
    public ControllerProfile Profile { get; } = profile;
    public bool IsInstalling { get; private set; }
    public bool CanInstall => !IsInstalling;
    public bool HasFeedback => FeedbackTitle.Length > 0;
    public string FeedbackTitle { get; private set; } = "";
    public string FeedbackMessage { get; private set; } = "";
    public InfoBarSeverity Severity { get; private set; }
    public string ButtonText =>
        IsInstalling ? "Installing…"
        : _installed ? "Reinstall"
        : "Install";
    public string InstallLabel => $"{ButtonText} {Profile.Software} {Profile.Name}";
    private bool _installed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public async Task InstallAsync()
    {
        if (IsInstalling)
            return;
        IsInstalling = true;
        FeedbackTitle = "";
        Changed();
        try
        {
            await Task.Run(Profile.Install);
            _installed = true;
            Severity = InfoBarSeverity.Success;
            FeedbackTitle = "Profiles installed";
            FeedbackMessage = Profile.Instructions;
        }
        catch (Exception error)
        {
            Severity = InfoBarSeverity.Error;
            FeedbackTitle = "Could not install profiles";
            FeedbackMessage =
                $"{error.GetBaseException().Message} Check the application's profile folder and try again.";
        }
        finally
        {
            IsInstalling = false;
            Changed();
        }
    }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
