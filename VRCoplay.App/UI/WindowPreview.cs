// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
namespace VRCoplay;
internal sealed class WindowPreview : IDisposable, IAsyncDisposable
{
    private readonly FrameworkElement _host;
    private GraphicsCaptureItem? _item;
    private CanvasDevice? _device;
    private CompositionGraphicsDevice? _graphics;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private SpriteVisual? _visual;
    private CompositionRoundedRectangleGeometry? _corners;
    private CompositionGeometricClip? _clip;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _bufferSize, _contentSize;
    private long _presentedAt;
    private bool _hasFrame, _disposed;
    private bool _runningRequested, _startQueued;
    private Task _captureStopped = Task.CompletedTask;
    internal nint Source { get; }
    internal bool IsMonitor { get; }
    internal bool IsRunning => _session is not null;
    internal event Action? FirstFramePresented;
    internal event Action<Exception>? Failed;
    internal WindowPreview(FrameworkElement host, nint source, bool isMonitor = false)
    {
        _host = host;
        Source = source;
        IsMonitor = isMonitor;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Capture previews are unavailable on this device.");
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var pointer = isMonitor ? interop.CreateForMonitor(source, in iid) : interop.CreateForWindow(source, in iid);
            try { _item = GraphicsCaptureItem.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
            _item.Closed += SourceClosed;
            _contentSize = _item.Size;
            _device = new CanvasDevice();
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
            _surface = _graphics.CreateDrawingSurface(new Size(1, 1), Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            _brush = compositor.CreateSurfaceBrush(_surface);
            _brush.Stretch = CompositionStretch.Fill;
            _visual = compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            _visual.IsVisible = false;
            _corners = compositor.CreateRoundedRectangleGeometry();
            _corners.CornerRadius = new Vector2(12);
            _clip = compositor.CreateGeometricClip(_corners);
            _visual.Clip = _clip;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
            UpdateLayout();
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    internal void SetRunning(bool running)
    {
        if (_disposed)
            return;
        _runningRequested = running;
        if (!running)
            StopCapture();
        else if (!IsRunning && !_startQueued)
            _ = StartCaptureAsync();
    }
    private async Task StartCaptureAsync()
    {
        _startQueued = true;
        try
        {
            await Task.Yield();
            await _captureStopped;
            if (_disposed || !_runningRequested)
                return;
            _bufferSize = _item!.Size;
            if (_bufferSize.Width <= 0 || _bufferSize.Height <= 0)
                return;
            _pool = Direct3D11CaptureFramePool.Create(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _bufferSize);
            _pool.FrameArrived += FrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }
        catch (Exception error) { Fail(error); }
        finally { _startQueued = false; }
    }
    internal void UpdateLayout()
    {
        if (_disposed || _visual is null || _contentSize.Width <= 0 || _contentSize.Height <= 0)
            return;
        var fit = Math.Min(_host.ActualWidth / _contentSize.Width, _host.ActualHeight / _contentSize.Height);
        var width = (float)(_contentSize.Width * fit);
        var height = (float)(_contentSize.Height * fit);
        _visual.Size = _corners!.Size = new(width, height);
        _visual.Offset = new((float)(_host.ActualWidth - width) / 2, (float)(_host.ActualHeight - height) / 2, 0);
    }
    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed || sender != _pool)
            return;
        try
        {
            SizeInt32 size;
            var firstFrame = false;
            using (var frame = sender.TryGetNextFrame())
            {
                if (frame is null)
                    return;
                size = frame.ContentSize;
                if (size.Width <= 0 || size.Height <= 0)
                    return;
                if (size.Width <= _bufferSize.Width && size.Height <= _bufferSize.Height
                    && (!_hasFrame || Stopwatch.GetElapsedTime(_presentedAt).TotalMilliseconds >= 33))
                {
                    _contentSize = size;
                    UpdateLayout();
                    var scale = _host.XamlRoot.RasterizationScale;
                    var pixels = new Size(Math.Max(1, Math.Ceiling(_visual!.Size.X * scale)), Math.Max(1, Math.Ceiling(_visual.Size.Y * scale)));
                    if (_surface!.Size != pixels)
                        CanvasComposition.Resize(_surface, pixels);
                    using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
                    using (var draw = CanvasComposition.CreateDrawingSession(_surface))
                    {
                        draw.Clear(Colors.Transparent);
                        draw.DrawImage(bitmap, new Rect(0, 0, pixels.Width, pixels.Height), new Rect(0, 0, size.Width, size.Height));
                    }
                    _visual.IsVisible = true;
                    _presentedAt = Stopwatch.GetTimestamp();
                    firstFrame = !_hasFrame;
                    _hasFrame = true;
                }
            }
            if (size.Width != _bufferSize.Width || size.Height != _bufferSize.Height)
            {
                _bufferSize = size;
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
            if (firstFrame)
                FirstFramePresented?.Invoke();
        }
        catch (Exception error) { Fail(error); }
    }
    private void SourceClosed(GraphicsCaptureItem sender, object args) =>
        _host.DispatcherQueue.TryEnqueue(() => Fail(new InvalidOperationException("The selected capture source is unavailable.")));
    private void Fail(Exception error)
    {
        if (_disposed)
            return;
        StopCapture();
        Failed?.Invoke(error);
    }
    private void StopCapture()
    {
        _runningRequested = false;
        var session = _session;
        var pool = _pool;
        _session = null;
        _pool = null;
        if (pool is not null)
            pool.FrameArrived -= FrameArrived;
        if (session is null && pool is null)
            return;
        _captureStopped = Task.Run(() =>
        {
            Release(session);
            Release(pool);
        });
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopCapture();
        if (_item is not null)
            _item.Closed -= SourceClosed;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visual?.Dispose();
        _clip?.Dispose();
        _corners?.Dispose();
        _brush?.Dispose();
        _surface?.Dispose();
        _graphics?.Dispose();
        var device = _device;
        var stopped = _captureStopped;
        _device = null;
        _captureStopped = Task.Run(async () =>
        {
            await stopped.ConfigureAwait(false);
            Release(device);
        });
        _item = null;
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _captureStopped;
    }
    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception error) { Trace.TraceError($"Preview resource cleanup failed: {error}"); }
    }
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }
}
