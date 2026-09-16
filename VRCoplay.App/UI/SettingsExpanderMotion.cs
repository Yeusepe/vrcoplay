// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
namespace VRCoplay;
internal sealed class SettingsExpanderMotion : IDisposable
{
    private readonly SettingsExpander _expander;
    private readonly long _expandedSubscription, _visibilitySubscription;
    private readonly UISettings _motionSettings;
    private Grid? _reveal;
    private Storyboard? _transition;
    private double _collapsedHeight, _targetHeight;
    private bool _disposed;
    internal SettingsExpanderMotion(SettingsExpander expander, UISettings settings)
    {
        _expander = expander;
        _motionSettings = settings;
        expander.ApplyTemplate();
        Wrap();
        _expandedSubscription = expander.RegisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty,
            (_, _) => SetExpanded(expander.IsLoaded && expander.Visibility == Visibility.Visible && _motionSettings.AnimationsEnabled));
        _visibilitySubscription = expander.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (_reveal is not null)
                _reveal.Visibility = expander.Visibility;
            SetExpanded(animate: false);
        });
        expander.SizeChanged += ExpanderSizeChanged;
    }
    internal static IEnumerable<SettingsExpanderMotion> Attach(DependencyObject root, UISettings settings) =>
        root.FindDescendants().OfType<SettingsExpander>()
            .Where(expander => expander.FindAscendant<DependencyObject>(parent => parent == root || parent is SettingsExpander) == root)
            .ToArray().Select(expander => new SettingsExpanderMotion(expander, settings));
    private void Wrap()
    {
        if (VisualTreeHelper.GetParent(_expander) is not Panel parent)
            return;
        _collapsedHeight = _expander.IsExpanded
            ? _expander.FindDescendant<ToggleButton>(button => button.Name == "ExpanderHeader")?.ActualHeight ?? _expander.MinHeight
            : _expander.ActualHeight;
        var index = parent.Children.IndexOf(_expander);
        parent.Children.RemoveAt(index);
        _reveal = new Grid { Visibility = _expander.Visibility, RowDefinitions = { new() { Height = GridLength.Auto } } };
        _reveal.Children.Add(_expander);
        parent.Children.Insert(index, _reveal);
    }
    private void ExpanderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_expander.IsExpanded && _transition is null)
            _collapsedHeight = args.NewSize.Height;
        else if (_expander.IsExpanded && _transition is not null && Math.Abs(_targetHeight - args.NewSize.Height) > .5)
            SetExpanded(animate: true);
    }
    private void SetExpanded(bool animate)
    {
        if (_reveal is not { } reveal)
            return;
        var from = double.IsNaN(reveal.Height) ? reveal.ActualHeight : reveal.Height;
        _transition?.Stop();
        _transition = null;
        reveal.Height = from;
        if (!animate)
        {
            reveal.Height = double.NaN;
            return;
        }
        reveal.Measure(new(_expander.ActualWidth, double.PositiveInfinity));
        var to = _targetHeight = _expander.IsExpanded ? _expander.DesiredSize.Height : _collapsedHeight;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(280),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, reveal);
        Storyboard.SetTargetProperty(animation, nameof(FrameworkElement.Height));
        var transition = new Storyboard();
        transition.Children.Add(animation);
        transition.Completed += (_, _) =>
        {
            if (_transition != transition)
                return;
            reveal.Height = double.NaN;
            transition.Stop();
            _transition = null;
        };
        _transition = transition;
        transition.Begin();
    }
    internal void ApplySettings()
    {
        if (!_disposed && !_motionSettings.AnimationsEnabled)
            SetExpanded(animate: false);
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _expander.UnregisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, _expandedSubscription);
        _expander.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _visibilitySubscription);
        _expander.SizeChanged -= ExpanderSizeChanged;
        SetExpanded(animate: false);
    }
}
