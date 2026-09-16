// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed class OverlayRenderer(StreamOverlay state, int width, int height, int fps)
{
    private readonly Dictionary<(OverlayKind Kind, string? Code), Task<(Sprite Large, Sprite Small)>> _cache = [];
    private bool _ready;
    private readonly HashSet<(OverlayKind, string?)> _reported = [];
    private Task<PointerCalibrationRenderer>? _pointer;
    internal event Action<Exception>? Failed;
    internal bool AnimationsEnabled => state.AnimationsEnabled;
    internal bool Render(OverlayCanvas output, long now, bool available)
    {
        var p = state.Present(now, available && _ready, fps);
        _ready = false;
        if (!available || p.Kind == OverlayKind.None)
        {
            _ready = available;
            return false;
        }
        if (p.Kind == OverlayKind.PointerCalibration)
        {
            _pointer ??= Task.Run(() => new PointerCalibrationRenderer(width, height));
            if (!_pointer.IsCompleted) return false;
            if (_pointer.IsFaulted)
            {
                if (_reported.Add((p.Kind, null))) Failed?.Invoke(_pointer.Exception!.GetBaseException());
                return false;
            }
            _ready = true;
            if (p.Opacity <= 0) return false;
            _pointer.Result.Render(output, p);
            return true;
        }
        var key = (p.Kind, p.Code);
        if (!_cache.TryGetValue(key, out var prepare))
        {
            foreach (var old in _cache.Keys.Where(k => k.Kind == OverlayKind.Invite && k != key).ToArray())
                _cache.Remove(old);
            _cache[key] = prepare = Task.Run(() => Prepare(p.Kind, p.Code));
            return false;
        }
        if (!prepare.IsCompleted)
            return false;
        if (prepare.IsFaulted)
        {
            if (_reported.Add(key))
            {
                Failed?.Invoke(prepare.Exception!.GetBaseException());
            }
            return false;
        }
        _ready = true;
        if (p.Opacity <= 0)
            return false;
        var (large, small) = prepare.Result;
        large.Draw(output, 0, height - large.Height, (float)(p.Opacity * (1 - p.Compact)));
        small.Draw(output, 0, height - small.Height, (float)(p.Opacity * p.Compact));
        return true;
    }
    private (Sprite, Sprite) Prepare(OverlayKind kind, string? code)
    {
        var name = kind switch
        {
            OverlayKind.Invite => "invite",
            OverlayKind.Warning => "warning",
            OverlayKind.Calibration => "calibration",
            OverlayKind.CalibrationParty => "calibration-party",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var rgba = Bitmap.Load(name);
        if (kind == OverlayKind.Invite)
            rgba.AddCode(code!);
        var h = Math.Min(height, Math.Max(2, width / 8 * 2));
        var large = Sprite.Scale(rgba, width, h);
        return (large, kind == OverlayKind.Invite ? large : Sprite.Scale(Bitmap.Load(name + "-compact"), width, h));
    }
    internal sealed record Bitmap(int Width, int Height, byte[] Rgba)
    {
        internal static Bitmap Load(string name)
        {
            var (w, h) = name switch { "glyphs" => (1920, 96), "pointer-copy" => (1024, 1024), "pointer-marks" => (1024, 256), _ => (1024, 256) };
            var rgba = File.ReadAllBytes(
                Path.Combine(AppContext.BaseDirectory, "Assets", "Overlays", "Runtime", name + ".rgba")
            );
            if (rgba.Length != w * h * 4)
                throw new InvalidDataException("Invalid overlay artwork. Reinstall VRCoplay.");
            return new(w, h, rgba);
        }
        internal Bitmap Crop(int x, int y, int w, int h)
        {
            var bytes = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Rgba.AsSpan(((y + row) * Width + x) * 4, w * 4).CopyTo(bytes.AsSpan(row * w * 4));
            return new(w, h, bytes);
        }
        internal void AddCode(string code)
        {
            var glyphs = Load("glyphs");
            for (int n = 0; n < 6; n++)
            for (int y = 0; y < 96; y++)
            {
                int from = (y * glyphs.Width + StreamOverlay.Alphabet.IndexOf(code[n]) * 60) * 4;
                int to = ((152 + y) * Width + 600 + n * 60) * 4;
                glyphs.Rgba.AsSpan(from, 60 * 4).CopyTo(Rgba.AsSpan(to));
            }
        }
    }
    internal sealed record Sprite(int Width, int Height, Bitmap? Image = null)
    {
        internal (Sprite Image, float Opacity)[] Layers { get; set; } = [];
        internal static Sprite Scale(Bitmap bitmap, int w, int h) => new(w, h, bitmap);
        internal void Draw(OverlayCanvas output, int x, int y, float opacity, double reveal = 1)
        {
            if (Image is not null) output.Draw(Image, x, y, Width, Height, opacity, reveal);
            foreach (var layer in Layers) layer.Image.Draw(output, x, y, opacity * layer.Opacity, reveal);
        }
    }
}
