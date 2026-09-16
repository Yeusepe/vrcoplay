// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.User32.POINTER_FLAGS;
namespace VRCoplay;
internal sealed class WindowTouch : IDisposable
{
    private static readonly Lazy<bool> Initialized = new(() =>
        InitializeTouchInjection(1, TOUCH_FEEDBACK.TOUCH_FEEDBACK_DEFAULT)
            ? true
            : throw new Win32Exception(Marshal.GetLastWin32Error())
    );
    private readonly object _gate = new();
    private readonly SafeHSYNTHETICPOINTERDEVICE _pen;
    private readonly POINTER_TYPE_INFO[] _preview =
    [
        new()
        {
            type = POINTER_INPUT_TYPE.PT_PEN,
            penInfo = new() { pointerInfo = new() { pointerType = POINTER_INPUT_TYPE.PT_PEN } },
        },
    ];
    private readonly POINTER_TOUCH_INFO[] _contacts =
    [
        new()
        {
            pointerInfo = new() { pointerType = POINTER_INPUT_TYPE.PT_TOUCH, pointerId = 0 },
            touchMask = TOUCH_MASK.TOUCH_MASK_CONTACTAREA,
        },
    ];
    private bool _down,
        _armed,
        _disposed,
        _hovering;
    private nint _target;
    internal WindowTouch()
    {
        _ = Initialized.Value;
        _pen = CreateSyntheticPointerDevice(
            POINTER_INPUT_TYPE.PT_PEN,
            1,
            POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_INDIRECT
        );
        if (_pen.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal void Update(nint target, POINT? point, float trigger)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (target != _target || point is null)
            {
                Cancel();
                _target = target;
            }
            if (point is null)
                return;
            if (trigger < .5f)
            {
                End(POINTER_FLAG_UP);
                _armed = true;
                Hover(point);
                return;
            }
            if (!_down && (!_armed || trigger < .75f))
            {
                Hover(point);
                return;
            }
            Hover(null);
            _contacts[0].pointerInfo.ptPixelLocation = point.Value;
            _contacts[0].rcContact = new(point.Value.X - 2, point.Value.Y - 2, point.Value.X + 2, point.Value.Y + 2);
            Send(POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | (_down ? POINTER_FLAG_UPDATE : POINTER_FLAG_DOWN));
            _down = true;
        }
    }
    private void Cancel()
    {
        _armed = false;
        End(POINTER_FLAG_UP | POINTER_FLAG_CANCELED);
        Hover(null);
    }
    private void Hover(POINT? point)
    {
        if (!_hovering && point is null)
            return;
        ref var info = ref _preview[0].penInfo.pointerInfo;
        if (point is { } screen)
            info.ptPixelLocation = new(
                screen.X - GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN),
                screen.Y - GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN)
            );
        Send(POINTER_FLAG_UPDATE | (point is null ? POINTER_FLAG_NONE : POINTER_FLAG_INRANGE), pen: true);
        _hovering = point is not null;
    }
    internal static POINT? Map(ushort x, ushort y, RECT source, int? outputWidth, int? outputHeight)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return null;
        double u = x / 1919d,
            v = y / 941d;
        if (outputWidth is int width && outputHeight is int height)
        {
            var scale = Math.Min((double)width / source.Width, (double)height / source.Height);
            u = (u * width - (width - source.Width * scale) / 2) / (source.Width * scale);
            v = (v * height - (height - source.Height * scale) / 2) / (source.Height * scale);
        }
        return u is >= 0 and <= 1 && v is >= 0 and <= 1
            ? new POINT(
                source.left + (int)Math.Round(u * (source.Width - 1)),
                source.top + (int)Math.Round(v * (source.Height - 1))
            )
            : null;
    }
    private void End(POINTER_FLAGS flags)
    {
        if (!_down)
            return;
        _down = false;
        Send(flags);
    }
    private void Send(POINTER_FLAGS flags, bool pen = false)
    {
        if (pen)
            _preview[0].penInfo.pointerInfo.pointerFlags = flags;
        else
            _contacts[0].pointerInfo.pointerFlags = flags;
        bool Inject() => pen ? InjectSyntheticPointerInput(_pen, _preview, 1) : InjectTouchInput(1, _contacts);
        if (Inject())
            return;
        if (Marshal.GetLastWin32Error() == 21)
        {
            Thread.Sleep(1);
            if (Inject())
                return;
        }
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        _down = _armed = _hovering = false;
        _disposed = true;
        _pen.Dispose();
        throw error;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                Cancel();
            }
            finally
            {
                _disposed = true;
                _pen.Dispose();
            }
        }
    }
}
