// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace VRCoplay;
internal sealed class StreamOverlayTarget : IDisposable
{
    private readonly Gst.Element _element;
    private readonly OverlayCanvas _canvas = new();
    private readonly DrawCallback _callback;
    private readonly uint _handler;
    internal StreamOverlayTarget(Gst.Element element, Action<OverlayCanvas, double> draw, Action<Exception> failed)
    {
        _element = element;
        _callback = (_, _, target, _, duration, _) =>
        {
            try
            {
                _canvas.Begin(target);
                try { draw(_canvas, duration / 1_000_000_000d); }
                finally { _canvas.End(); }
                return 1;
            }
            catch (Exception error) { failed(error); return 0; }
        };
        _handler = g_signal_connect_data(element.Handle, "draw", _callback, 0, 0, 0);
    }
    public void Dispose()
    {
        g_signal_handler_disconnect(_element.Handle, _handler);
        _element.Dispose(); _canvas.Dispose();
        GC.KeepAlive(_callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrawCallback(nint overlay, nint device, nint target, ulong timestamp, ulong duration, nint data);
    [DllImport("gobject-2.0-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint g_signal_connect_data(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, DrawCallback callback, nint data, nint destroy, int flags);
    [DllImport("gobject-2.0-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_signal_handler_disconnect(nint instance, uint handler);
}
