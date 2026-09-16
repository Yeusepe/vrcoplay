// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using static VRCoplay.OverlayRenderer;
namespace VRCoplay;
internal sealed class PointerCalibrationRenderer
{
    private readonly int _width, _height, _textWidth, _markSize;
    private readonly Sprite[] _text, _progress, _marks;
    private readonly Sprite _fill, _check;
    private readonly Sprite[] _ripples;
    private readonly TextTransition _title, _instruction, _progressText;
    private readonly Spring[] _active = new Spring[4], _done = new Spring[4];
    private readonly bool[] _wasDone = new bool[4];
    private readonly double[] _doneAge = new double[4];
    private Spring _centerOffset;
    private double _time = double.NaN;
    internal PointerCalibrationRenderer(int width, int height)
    {
        (_width, _height) = (width, height);
        _textWidth = Even(Math.Min(width * .76, height * 1.45));
        _markSize = Even(Math.Min(width, height) * .14);
        var copy = Bitmap.Load("pointer-copy");
        _text = new Sprite[16];
        foreach (var i in Enumerable.Range(1, 15))
            _text[i] = Sprite.Scale(copy.Crop(0, i * 64, 1024, 64), _textWidth, Even(_textWidth / 16d));
        _progress = Enumerable.Range(0, 4).Select(i => Sprite.Scale(copy.Crop(i * 256, 0, 256, 64),
            Even(_textWidth / 4d), Even(_textWidth / 16d))).ToArray();
        var marks = Bitmap.Load("pointer-marks");
        _marks = new Sprite[4];
        foreach (var i in new[] { 1, 3 })
            _marks[i] = Sprite.Scale(marks.Crop(i * 128, 0, 128, 128), _markSize, _markSize);
        _fill = Sprite.Scale(marks.Crop(0, 128, 128, 128), _markSize, _markSize);
        _check = Sprite.Scale(marks.Crop(128, 128, 128, 128), _markSize, _markSize);
        var halo = marks.Crop(384, 0, 128, 128);
        _ripples = Enumerable.Range(0, 20).Select(i => {
            int size = Even(_markSize * (.72 + .38 * i / 19));
            return Sprite.Scale(halo, size, size);
        }).ToArray();
        _title = new([_text[1], _text[3], _text[5], _text[7], _text[9], _text[11], _text[13], _text[14]], fadeThrough: true);
        _instruction = new([_text[2], _text[4], _text[6], _text[8], _text[10], _text[12], _text[15]], fadeThrough: true);
        _progressText = new(_progress);
    }
    internal void Render(OverlayCanvas output, OverlayPresentation p)
    {
        double visibility = Ease(p.Opacity);
        var opacity = Alpha(visibility);
        output.Dim((float)(visibility * .64));
        bool success = p.Step == 5;
        bool snap = double.IsNaN(_time) || p.Time < _time || p.Motion < 0;
        double dt = snap ? 0 : Math.Max(0, p.Time - _time);
        _time = p.Time;
        int title = p.Issue switch {
            PointerCalibrationIssue.NoSignal => 3, PointerCalibrationIssue.Miss => 4,
            PointerCalibrationIssue.OutsidePlane => 5, PointerCalibrationIssue.Duplicate => 6,
            PointerCalibrationIssue.InvalidGeometry => 7, _ => 1
        };
        int instruction = title switch { 3 => 3, 4 => 4, 5 => 5, 7 => 6, _ => 1 };
        _title.Update(success ? 2 : p.Retry ? title : 0, dt, snap);
        _instruction.Update(success ? 2 : p.Retry ? instruction : 0, dt, snap);
        _progressText.Update(success ? -1 : Math.Clamp(p.Step - 1, 0, 3), dt, snap);
        _centerOffset.Update(success ? -22 : 0, dt, snap);
        double cue = p.Motion < 0 ? 0 : Math.Pow(Math.Max(0, 1 - p.Motion / .6), 2);
        int textX = (_width - _textWidth) / 2;
        double unit = _textWidth / 1024d;
        int entrance = p.Motion < 0 ? 0 : (int)Math.Round(6 * unit * (1 - visibility));
        int centerY = _height / 2 + (int)Math.Round(_centerOffset.Value * unit) + entrance;
        int progressWidth = Even(_textWidth / 4d);
        Draw(output, _progressText.Frame, progressWidth, (_width - progressWidth) / 2,
            _height / 2 - (int)(44 * unit) - _progressText.Frame.Height / 2 + entrance, opacity);
        Draw(output, _title.Frame, _textWidth, textX, centerY - _title.Frame.Height / 2, opacity);
        Draw(output, _instruction.Frame, _textWidth, textX, centerY + (int)(48 * unit) - _instruction.Frame.Height / 2, opacity);
        for (int i = 0; i < 4; i++)
        {
            var uv = PointerCalibration.Target(i);
            int x = (int)Math.Round(uv.X * (_width - 1)), y = (int)Math.Round(uv.Y * (_height - 1));
            bool done = success || i < p.Step - 1;
            bool active = i == p.Step - 1 && !success;
            _active[i].Update(active && !done ? 1 : 0, dt, snap);
            _done[i].Update(done ? 1 : 0, dt, snap);
            _doneAge[i] = snap ? 1 : done && !_wasDone[i] ? 0 : _doneAge[i] + dt;
            _wasDone[i] = done;
            if (done && p.Motion >= 0 && _doneAge[i] < .45)
            {
                double t = _doneAge[i] / .45;
                var ripple = _ripples[(int)Math.Round(Ease(t) * (_ripples.Length - 1))];
                Draw(output, ripple, ripple.Height, x - ripple.Height / 2, y - ripple.Height / 2,
                    Alpha(visibility * .8 * Math.Pow(1 - t, 2)));
            }
            Draw(output, _marks[1], _markSize, x - _markSize / 2, y - _markSize / 2,
                Alpha(visibility * _active[i].Value));
            double confirmation = visibility * _done[i].Value *
                (success ? 1 : .32 + .68 * (1 - Ease((_doneAge[i] - .32) / .4)));
            Draw(output, _fill, _markSize, x - _markSize / 2, y - _markSize / 2, Alpha(confirmation));
            Draw(output, _check, _markSize, x - _markSize / 2, y - _markSize / 2, Alpha(confirmation),
                reveal: Math.Clamp((_doneAge[i] - .04) / .24, 0, 1));
            if (_active[i].Value > .001)
                Draw(output, _marks[3], _markSize, x - _markSize / 2, y - _markSize / 2,
                    Alpha(visibility * _active[i].Value * (.22 + .5 * cue)));
        }
    }
    private void Draw(OverlayCanvas target, Sprite sprite, int w, int x, int y, int opacity, double reveal = 1)
    {
        x = Math.Clamp(x, 0, Math.Max(0, _width - w));
        y = Math.Clamp(y, 0, Math.Max(0, _height - sprite.Height));
        if (w > _width || sprite.Height > _height || opacity <= 0 || reveal <= 0) return;
        sprite.Draw(target, x, y, opacity / 255f, reveal);
    }
    private static double Ease(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    private static int Alpha(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private struct Spring
    {
        internal double Value;
        private double _velocity;
        internal void Update(double target, double dt, bool snap)
        {
            if (snap) { Value = target; _velocity = 0; return; }
            double offset = Value - target, b = _velocity + 24 * offset, decay = Math.Exp(-24 * dt);
            Value = target + (offset + b * dt) * decay;
            _velocity = (_velocity - 24 * b * dt) * decay;
            if (Math.Abs(Value - target) + Math.Abs(_velocity) < .0001) { Value = target; _velocity = 0; }
        }
    }
    internal sealed class TextTransition(Sprite[] frames, bool fadeThrough = false)
    {
        private readonly Spring[] _weights = new Spring[frames.Length];
        internal Sprite Frame { get; } = new(frames[0].Width, frames[0].Height);
        internal void Update(int selected, double dt, bool snap)
        {
            var layers = new List<(Sprite, float)>();
            for (int i = 0; i < frames.Length; i++)
            {
                _weights[i].Update(i == selected ? 1 : 0, dt, snap);
                double weight = Math.Clamp(_weights[i].Value, 0, 1);
                if (fadeThrough) weight = Ease(Math.Max(0, weight * 2 - 1));
                if (weight > .0001) layers.Add((frames[i], (float)weight));
            }
            Frame.Layers = layers.ToArray();
        }
    }
    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
