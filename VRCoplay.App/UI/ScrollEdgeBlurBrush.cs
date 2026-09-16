// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.WinUI;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
namespace VRCoplay;
public sealed partial class ScrollEdgeBlurBrush : XamlCompositionBrushBase
{
    [GeneratedDependencyProperty(DefaultValueCallback = nameof(DefaultTintColor))]
    public partial Color TintColor { get; set; }
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool EffectsEnabled { get; set; }
    private static object DefaultTintColor() => Colors.Transparent;
    partial void OnPropertyChanged(DependencyPropertyChangedEventArgs e) => Refresh();
    public bool Reverse { get; set; }
    public double BlurAmount { get; set; }
    public double StartOffset { get; set; }
    public double EndOffset { get; set; } = 1;
    private readonly UISettings _settings = new();
    private readonly List<IDisposable> _resources = [];
    private bool _connected;
    protected override void OnConnected()
    {
        if (_connected)
            return;
        _connected = true;
        _settings.AdvancedEffectsEnabledChanged += EffectsChanged;
        Refresh();
    }
    private void EffectsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void Refresh()
    {
        if (!_connected)
            return;
        Release();
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        if (!EffectsEnabled)
        {
            CompositionBrush = Keep(compositor.CreateColorBrush(Colors.Transparent));
            return;
        }
        CompositionBrush source = Keep(compositor.CreateColorBrush(BlurAmount > 0 ? Colors.Transparent : TintColor));
        if (BlurAmount > 0 && _settings.AdvancedEffectsEnabled && new CompositionCapabilities().AreEffectsSupported())
        {
            try
            {
                var blur = Keep(
                    new GaussianBlurEffect
                    {
                        BlurAmount = (float)BlurAmount,
                        BorderMode = EffectBorderMode.Hard,
                        Source = new CompositionEffectSourceParameter("Backdrop"),
                    }
                );
                var factory = Keep(compositor.CreateEffectFactory(blur));
                var brush = Keep(factory.CreateBrush());
                brush.SetSourceParameter("Backdrop", Keep(compositor.CreateBackdropBrush()));
                source = brush;
            }
            catch (Exception error) when (error is ArgumentException or COMException)
            {
                Debug.WriteLine($"Scroll-edge blur unavailable: {error.Message}");
            }
        }
        var mask = Keep(compositor.CreateLinearGradientBrush());
        mask.StartPoint = new Vector2(0, Reverse ? 1 : 0);
        mask.EndPoint = new Vector2(0, Reverse ? 0 : 1);
        mask.ColorStops.Add(Keep(compositor.CreateColorGradientStop((float)StartOffset, Colors.Transparent)));
        mask.ColorStops.Add(Keep(compositor.CreateColorGradientStop((float)EndOffset, Colors.White)));
        var masked = Keep(compositor.CreateMaskBrush());
        masked.Source = source;
        masked.Mask = mask;
        CompositionBrush = masked;
    }
    private T Keep<T>(T resource)
        where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }
    private void Release()
    {
        CompositionBrush = null;
        for (var i = _resources.Count - 1; i >= 0; i--)
            _resources[i].Dispose();
        _resources.Clear();
    }
    protected override void OnDisconnected()
    {
        _connected = false;
        _settings.AdvancedEffectsEnabledChanged -= EffectsChanged;
        Release();
    }
}
