// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Foundation;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private WindowPreview? _preview;
    private CaptureSource? _previewSource;
    private CaptureSource? _previewFailedSource;
    private bool _previewSuspended;
    private XamlRoot? _previewXamlRoot;
    private bool _previewUpdateQueued;
    private Task _previewCleanup = Task.CompletedTask;
    private void InitializePreview()
    {
        PreviewHost.EffectiveViewportChanged += (_, _) => UpdatePreview();
        PreviewHost.SizeChanged += (_, _) => UpdatePreview();
        PreviewHost.Unloaded += (_, _) => ClosePreview();
        RootPage.Loaded += (_, _) =>
        {
            if (_previewXamlRoot is not null)
                _previewXamlRoot.Changed -= PreviewRootChanged;
            _previewXamlRoot = RootPage.XamlRoot;
            _previewXamlRoot.Changed += PreviewRootChanged;
            UpdatePreview();
        };
        AppWindow.Changed += (_, _) => UpdatePreview();
        Closed += (_, _) =>
        {
            if (_previewXamlRoot is not null)
                _previewXamlRoot.Changed -= PreviewRootChanged;
        };
    }
    private void PreviewRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdatePreview();
    private void RefreshWindows()
    {
        _previewFailedSource = null;
        WindowPicker.RefreshSources();
        UpdatePreview();
    }
    private void WindowPicker_SelectionChanged(object? sender, EventArgs e)
    {
        if (WindowPicker.IsRefreshing)
            return;
        _previewFailedSource = null;
        UpdatePreview();
        _session.SelectSource(WindowPicker.SelectedSource);
    }
    private bool PreviewIsVisible()
    {
        if (_windowClosed || _closing || _previewSuspended || !_windowActive
            || !PreviewHost.IsLoaded || RootPage.XamlRoot?.IsHostVisible != true
            || PreviewPanel.Visibility != Visibility.Visible
            || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
            || PreviewHost.ActualWidth <= 0 || PreviewHost.ActualHeight <= 0)
            return false;
        var bounds = PreviewHost.TransformToVisual(Scroller).TransformBounds(
            new Rect(0, 0, PreviewHost.ActualWidth, PreviewHost.ActualHeight));
        bounds.Intersect(new Rect(0, 0, Scroller.ActualWidth, Scroller.ActualHeight));
        return bounds.Width > 0 && bounds.Height > 0;
    }
    private void UpdatePreview()
    {
        if (_previewUpdateQueued || _windowClosed || _closing)
            return;
        _previewUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _previewUpdateQueued = false;
            if (!_windowClosed && !_closing)
                UpdatePreviewCore();
        });
    }
    private void UpdatePreviewCore()
    {
        var source = WindowPicker.SelectedSource;
        if (_preview is not null && _previewSource?.IsSameSource(source) != true)
            ClosePreview();
        _preview?.UpdateLayout();
        if (!PreviewIsVisible() || source is null)
        {
            _preview?.SetRunning(false);
            return;
        }
        if (_preview is null && !source.IsSameSource(_previewFailedSource))
        {
            try
            {
                var preview = new WindowPreview(PreviewHost, source.CurrentHandle(), source is CaptureMonitor);
                _preview = preview;
                _previewSource = source;
                preview.FirstFramePresented += () =>
                {
                    if (_preview == preview)
                        PreviewEmpty.Visibility = Visibility.Collapsed;
                };
                preview.Failed += error =>
                {
                    if (_preview == preview)
                        PreviewFailed(source, error);
                };
            }
            catch (Exception error) { PreviewFailed(source, error); }
        }
        _preview?.SetRunning(true);
    }
    private void PreviewFailed(CaptureSource source, Exception error)
    {
        Debug.WriteLine(error);
        ClosePreview();
        _previewFailedSource = source;
        PreviewPlaceholder.Text = "Preview unavailable. Open Capture source to choose a window or display.";
    }
    private void ClosePreview()
    {
        var preview = _preview;
        _preview = null;
        _previewSource = null;
        if (preview is not null)
        {
            var cleanup = preview.DisposeAsync().AsTask();
            _previewCleanup = _previewCleanup.IsCompletedSuccessfully ? cleanup : Task.WhenAll(_previewCleanup, cleanup);
        }
        PreviewPlaceholder.Text = "Choose a window or display to see it here";
        PreviewEmpty.Visibility = Visibility.Visible;
    }
    private void WindowPicker_DropDownOpened(object sender, object e) => RefreshWindows();
    private void ResizePreview()
    {
        var preview = PreviewPanel.Visibility == Visibility.Visible;
        var wide = preview && RootPage.ActualWidth >= 1100;
        ControlColumn.Width = new(wide ? 420 : 0);
        Grid.SetRow(PreviewPanel, wide ? 0 : 1);
        Grid.SetColumn(StreamPanel, wide ? 1 : 0);
        StreamPanel.Margin = wide ? new(24, 0, 0, 0) : default;
        StreamPanel.MaxWidth = preview ? double.PositiveInfinity : 440;
        PreviewPanel.Margin = wide ? default : new(0, 24, 0, 0);
        PreviewHost.Height = PreviewHost.ActualWidth * 9 / 16;
    }
}
