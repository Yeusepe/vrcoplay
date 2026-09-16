// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace VRCoplay.NativeRtc;
internal static unsafe partial class Rtc
{
    [LibraryImport("datachannel", EntryPoint = "rtcSetRemoteDescription", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rtcSetRemoteDescription(int pc, string sdp, string type);
    [LibraryImport("datachannel", EntryPoint = "rtcCreateDataChannelEx", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rtcCreateDataChannelEx(int pc, string label, in rtcDataChannelInit config);
    [LibraryImport("datachannel", EntryPoint = "rtcGetLocalDescription")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rtcGetLocalDescription(int pc, nint buffer, int size);
    [LibraryImport("datachannel", EntryPoint = "rtcGetRemoteAddress")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rtcGetRemoteAddress(int pc, nint buffer, int size);
}
