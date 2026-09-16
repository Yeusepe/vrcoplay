// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Microsoft.UI.Xaml;
namespace VRCoplay;
public sealed class CapturePickerEntry : INotifyPropertyChanged
{
    internal CaptureSource? Source { get; init; }
    internal string ApplicationId { get; init; } = "";
    public string Label { get; init; } = "";
    public object? IconWindow { get; init; }
    public bool IsHeader { get; init; }
    public IReadOnlyList<CapturePickerEntry> Children { get; init; } = [];
    public bool IsInteractive => !IsHeader;
    public double RowHeight => IsHeader ? 36 : 44;
    public string WindowCount => $"{Children.Count} windows";
    public string AccessibleName => IsHeader ? Label : Children.Count > 0 ? $"{Label}, {WindowCount}" : Source!.Description;
    public string Glyph => Source is CaptureMonitor ? "\uE7F4" : "\uE737";
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImageVisibility => IconWindow is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => IconWindow is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CountVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public string CurrentStatus => !IsCurrent ? "" : Children.Count > 0 ? "Contains selected window" : "Selected";
    private bool _isExpanded, _isCurrent;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; Changed(nameof(IsExpanded)); } }
    public bool IsCurrent { get => _isCurrent; set { if (_isCurrent == value) return; _isCurrent = value; Changed(nameof(CurrentVisibility)); Changed(nameof(CurrentStatus)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    internal bool Contains(CaptureSource? source) => source is not null && (source.IsSameSource(Source) || Children.Any(child => source.IsSameSource(child.Source)));
    internal static IReadOnlyList<CapturePickerEntry> Group(IEnumerable<CaptureWindow> windows, IReadOnlyList<CaptureMonitor> monitors)
    {
        var apps = windows.GroupBy(window => window.ApplicationId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().ApplicationName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var siblings = group.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
                return siblings.Length == 1 ? ForSource(siblings[0]) : new CapturePickerEntry
                {
                    ApplicationId = group.Key, Label = siblings[0].ApplicationName, IconWindow = siblings[0],
                    Children = siblings.Select(window => ForSource(window, child: true)).ToArray(),
                };
            }).ToArray();
        return new[] { new CapturePickerEntry { Label = apps.Length > 0 ? "Window Capture" : "No windows available", IsHeader = true } }
            .Concat(apps).Concat(new[] { new CapturePickerEntry { Label = "Fullscreen Capture", IsHeader = true } })
            .Concat(monitors.Select(monitor => ForSource(monitor))).ToArray();
    }
    private static CapturePickerEntry ForSource(CaptureSource source, bool child = false) => new()
    {
        Source = source, Label = source.Label,
        ApplicationId = source is CaptureWindow window ? window.ApplicationId : "",
        IconWindow = !child && source is CaptureWindow ? source : null,
    };
}
