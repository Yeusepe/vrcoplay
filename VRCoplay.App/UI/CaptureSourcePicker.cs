// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
public sealed partial class CaptureSourcePicker : UserControl
{
    private CaptureSource? _selected;
    private bool _initialized;
    private bool _isDropDownOpen;
    private XamlRoot? _flyoutRoot;
    internal bool IsRefreshing { get; private set; }
    internal CaptureSource? SelectedSource => _selected;
    internal IReadOnlyList<CaptureSource> Sources { get; private set; } = [];
    internal IReadOnlyList<CapturePickerEntry> Entries { get; private set; } = [];
    public event EventHandler? SelectionChanged;
    public event EventHandler? DropDownOpened;
    public object? SelectedItem
    {
        get => _selected;
        set => SelectSource(value as CaptureSource);
    }
    public bool IsDropDownOpen
    {
        get => _isDropDownOpen;
        set { if (value) SourceFlyout.ShowAt(PickerButton); else SourceFlyout.Hide(); }
    }
    public CaptureSourcePicker()
    {
        InitializeComponent();
        SourceFlyout.Opening += (_, _) =>
        {
            _isDropDownOpen = true;
            DropDownOpened?.Invoke(this, EventArgs.Empty);
            ResizeFlyout();
            _flyoutRoot = XamlRoot;
            _flyoutRoot.Changed += RootChanged;
        };
        SourceFlyout.Opened += (_, _) =>
        {
            var current = Entries.FirstOrDefault(entry => entry.Contains(_selected));
            if (current?.IsExpanded == true)
                current = current.Children.FirstOrDefault(entry => entry.Contains(_selected)) ?? current;
            if (current is not null && SourceTree.ContainerFromItem(current) is Control item)
                item.Focus(FocusState.Programmatic);
            else
                SourceTree.Focus(FocusState.Programmatic);
        };
        SourceFlyout.Closed += (_, _) =>
        {
            _isDropDownOpen = false;
            if (_flyoutRoot is not null) _flyoutRoot.Changed -= RootChanged;
            _flyoutRoot = null;
        };
        Unloaded += (_, _) => SourceFlyout.Hide();
    }
    public new bool Focus(FocusState value) => PickerButton.Focus(value);
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeFlyout();
    private void ResizeFlyout()
    {
        if (XamlRoot is not { } root) return;
        FlyoutBody.Width = Math.Max(0, Math.Min(Math.Max(360, ActualWidth - 16), root.Size.Width - 48));
        FlyoutBody.MaxHeight = Math.Max(80, Math.Min(480, root.Size.Height - 96));
    }
    internal void RefreshSources()
    {
        var previous = SelectedSource;
        var windows = CaptureWindow.Enumerate()
            .Concat(previous is CaptureWindow window && IsWindow(window.Hwnd)
                && GetWindowThreadProcessId(window.Hwnd, out var owner) != 0 && owner == window.Pid ? [window] : [])
            .DistinctBy(x => x.Hwnd).ToArray();
        SetSources(windows, CaptureMonitor.Enumerate());
    }
    internal void SetSources(IEnumerable<CaptureWindow> windows, IReadOnlyList<CaptureMonitor> monitors)
    {
        var ordered = windows.OrderBy(window => window.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var expanded = Entries.Where(entry => entry.IsExpanded).Select(entry => entry.ApplicationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = _selected;
        Sources = ordered.Cast<CaptureSource>().Concat(monitors).ToArray();
        Entries = CapturePickerEntry.Group(ordered, monitors);
        foreach (var entry in Entries)
            entry.IsExpanded = expanded.Contains(entry.ApplicationId);
        IsRefreshing = true;
        try
        {
            SourceTree.ItemsSource = Entries;
            SelectSource(Sources.FirstOrDefault(source => source.IsSameSource(previous))
                ?? (!_initialized && previous is null ? ordered.FirstOrDefault() : null));
            _initialized = true;
        }
        finally { IsRefreshing = false; }
    }
    private void SelectSource(CaptureSource? source)
    {
        var changed = source is null ? _selected is not null : !source.IsSameSource(_selected);
        _selected = source;
        SelectedLabel.Text = source?.Label ?? "Choose a window or display";
        SelectedImage.Source = source is CaptureWindow ? WindowIconImage.For(source) : null;
        SelectedImage.Visibility = source is CaptureWindow ? Visibility.Visible : Visibility.Collapsed;
        SelectedDisplay.Visibility = source is CaptureMonitor ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PickerButton, source?.Description);
        AutomationProperties.SetHelpText(PickerButton, source?.Description ?? "Choose an application window or a fullscreen display.");
        foreach (var entry in Entries.SelectMany(entry => entry.Children.Prepend(entry)))
            entry.IsCurrent = entry.Contains(source);
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void SourceInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is CapturePickerEntry entry) InvokeEntry(entry);
    }
    internal void InvokeEntry(CapturePickerEntry entry)
    {
        if (entry.Children.Count > 0)
            entry.IsExpanded = !entry.IsExpanded;
        else if (entry.Source is { } source)
        {
            SelectSource(source);
            SourceFlyout.Hide();
        }
    }
}
