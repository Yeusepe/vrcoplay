// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using PixelFormat = Vortice.DCommon.PixelFormat;
using AlphaMode = Vortice.DCommon.AlphaMode;
namespace VRCoplay;
internal sealed class OverlayCanvas : IDisposable
{
    private readonly ID2D1Factory1 _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
    private ID2D1Device? _device;
    private ID2D1DeviceContext? _context;
    private nint _deviceId;
    private readonly Dictionary<OverlayRenderer.Bitmap, ID2D1Bitmap> _images = [];
    private readonly Dictionary<string, ID2D1Bitmap> _files = [];
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal void Begin(nint renderTarget)
    {
        if (_images.Count > 128)
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
        System.Runtime.InteropServices.Marshal.AddRef(renderTarget);
        using var view = new ID3D11RenderTargetView(renderTarget);
        using var d3d = view.Device;
        if (_deviceId != d3d.NativePointer)
        {
            ReleaseDevice();
            using var dxgi = d3d.QueryInterface<IDXGIDevice>();
            _device = _factory.CreateDevice(dxgi);
            _context = _device.CreateDeviceContext(DeviceContextOptions.None);
            _deviceId = d3d.NativePointer;
        }
        using var resource = view.Resource;
        using var surface = resource.QueryInterface<IDXGISurface>();
        var desc = surface.Description;
        Width = (int)desc.Width; Height = (int)desc.Height;
        using var target = _context!.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new PixelFormat(desc.Format, AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _context.Target = target;
        _context.BeginDraw();
    }
    internal void End()
    {
        try { _context!.EndDraw().CheckError(); }
        finally { _context!.Target = null; }
    }
    internal unsafe void Draw(OverlayRenderer.Bitmap image, float x, float y, float width, float height, float opacity, double reveal = 1)
    {
        if (opacity <= 0 || reveal <= 0) return;
        if (!_images.TryGetValue(image, out var bitmap))
        {
            var rgba = (byte[])image.Rgba.Clone();
            for (int i = 0; i < rgba.Length; i += 4)
                for (int c = 0; c < 3; c++) rgba[i + c] = (byte)((rgba[i + c] * rgba[i + 3] + 127) / 255);
            fixed (byte* pixels = rgba)
                bitmap = _context!.CreateBitmap(new SizeI(image.Width, image.Height), (nint)pixels, (uint)(image.Width * 4),
                    new BitmapProperties1(new PixelFormat(Format.R8G8B8A8_UNorm, AlphaMode.Premultiplied)));
            _images.Add(image, bitmap);
        }
        var clip = reveal >= 1 ? width : width * (float)(.39 + .25 * reveal);
        _context!.PushAxisAlignedClip(new RawRectF(x, y, x + clip, y + height), AntialiasMode.Aliased);
        _context.DrawBitmap(bitmap, new RawRectF(x, y, x + width, y + height), opacity, InterpolationMode.Linear, null, null);
        _context.PopAxisAlignedClip();
    }
    internal void Fill(float x, float y, float width, float height, Color4 color)
    {
        using var brush = _context!.CreateSolidColorBrush(color);
        _context.FillRectangle(new RawRectF(x, y, x + width, y + height), brush);
    }
    internal void Dim(float opacity) => Fill(0, 0, Width, Height, new Color4(.033f, .043f, .073f, opacity));
    internal void Standby(string assets)
    {
        ID2D1Bitmap Load(string name)
        {
            if (_files.TryGetValue(name, out var bitmap)) return bitmap;
            using var wic = new IWICImagingFactory();
            using var decoder = wic.CreateDecoderFromFileName(Path.Combine(assets, name));
            using var frame = decoder.GetFrame(0);
            using var convert = wic.CreateFormatConverter();
            convert.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA);
            bitmap = _context!.CreateBitmapFromWicBitmap(convert);
            _files.Add(name, bitmap);
            return bitmap;
        }
        _context!.DrawBitmap(Load("background.png"), new RawRectF(0, 0, Width, Height), 1, InterpolationMode.Linear, null, null);
        var logo = Load("logo.png");
        var w = (float)Math.Max(2, Math.Min(Width * .172, Height * .5));
        var h = w * logo.Size.Height / logo.Size.Width;
        var x = Width - w - Math.Max(1, Width * 3 / 100);
        var y = Height - h - Math.Max(1, Height * 4 / 100);
        _context.DrawBitmap(logo, new RawRectF(x, y, x + w, y + h), 1, InterpolationMode.Linear, null, null);
    }
    private void ReleaseDevice()
    {
        foreach (var bitmap in _images.Values.Concat(_files.Values)) bitmap.Dispose();
        _images.Clear(); _files.Clear();
        _context?.Dispose(); _device?.Dispose();
    }
    public void Dispose() { ReleaseDevice(); _factory.Dispose(); }
}
