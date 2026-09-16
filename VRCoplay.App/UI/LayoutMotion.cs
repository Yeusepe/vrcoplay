// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
namespace VRCoplay;
internal sealed class LayoutMotion : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly UISettings _settings;
    private readonly Dictionary<UIElement, long> _subscriptions = new();
    private readonly HashSet<UIElement> _appearing = new();
    private readonly ImplicitAnimationCollection _reposition;
    private readonly ScalarKeyFrameAnimation _show;
    private readonly ScalarKeyFrameAnimation _hide;
    private bool _enabled;
    private bool _disposed;
    internal LayoutMotion(FrameworkElement root, IEnumerable<UIElement> elements, UISettings settings)
    {
        _root = root;
        _settings = settings;
        var compositor = ElementCompositionPreview.GetElementVisual(root).Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(0, 1));
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.Target = "Offset";
        move.Duration = TimeSpan.FromMilliseconds(280);
        move.InsertExpressionKeyFrame(1, "this.FinalValue", easing);
        _reposition = compositor.CreateImplicitAnimationCollection();
        _reposition["Offset"] = move;
        _show = compositor.CreateScalarKeyFrameAnimation();
        _show.Target = "Opacity";
        _show.DelayTime = TimeSpan.FromMilliseconds(120);
        _show.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _show.Duration = TimeSpan.FromMilliseconds(160);
        _show.InsertKeyFrame(0, 0);
        _show.InsertKeyFrame(1, 1, easing);
        _hide = compositor.CreateScalarKeyFrameAnimation();
        _hide.Target = "Opacity";
        _hide.Duration = TimeSpan.FromMilliseconds(80);
        _hide.InsertKeyFrame(1, 0, easing);
        foreach (var element in elements.Distinct())
            _subscriptions[element] = element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, VisibilityChanged);
        _root.LayoutUpdated += LayoutUpdated;
        ApplySettings();
    }
    private void VisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        var element = (UIElement)sender;
        ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations = null;
        if (element.Visibility == Visibility.Visible)
            _appearing.Add(element);
        else
            _appearing.Remove(element);
    }
    private void LayoutUpdated(object? sender, object args)
    {
        foreach (var element in _appearing)
            ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations = _enabled ? _reposition : null;
        _appearing.Clear();
    }
    internal void ApplySettings() => SetAnimationsEnabled(_settings.AnimationsEnabled);
    private void SetAnimationsEnabled(bool enabled)
    {
        _enabled = enabled;
        foreach (var element in _subscriptions.Keys)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.ImplicitAnimations = _enabled && element.Visibility == Visibility.Visible ? _reposition : null;
            ElementCompositionPreview.SetImplicitShowAnimation(element, _enabled ? _show : null);
            ElementCompositionPreview.SetImplicitHideAnimation(element, _enabled ? _hide : null);
            if (!_enabled)
            {
                var destination = visual.Offset;
                visual.StopAnimation("Offset");
                visual.Offset = destination;
                visual.StopAnimation("Opacity");
                visual.Opacity = 1;
            }
        }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _root.LayoutUpdated -= LayoutUpdated;
        foreach (var element in _subscriptions.Keys)
        {
            element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _subscriptions[element]);
            ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations = null;
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            ElementCompositionPreview.SetImplicitHideAnimation(element, null);
        }
        _appearing.Clear();
    }
}
