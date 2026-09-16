using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace VRCoplay.NativeRtc;
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal enum rtcState
{
    RTC_NEW = 0,
    RTC_CONNECTING = 1,
    RTC_CONNECTED = 2,
    RTC_DISCONNECTED = 3,
    RTC_FAILED = 4,
    RTC_CLOSED = 5,
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal enum rtcGatheringState
{
    RTC_GATHERING_NEW = 0,
    RTC_GATHERING_INPROGRESS = 1,
    RTC_GATHERING_COMPLETE = 2,
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal enum rtcCertificateType
{
    RTC_CERTIFICATE_DEFAULT = 0,
    RTC_CERTIFICATE_ECDSA = 1,
    RTC_CERTIFICATE_RSA = 2,
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal enum rtcTransportPolicy
{
    RTC_TRANSPORT_POLICY_ALL = 0,
    RTC_TRANSPORT_POLICY_RELAY = 1,
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal unsafe partial struct rtcConfiguration
{
    [NativeTypeName("const char **")]
    public sbyte** iceServers;
    public int iceServersCount;
    [NativeTypeName("const char *")]
    public sbyte* proxyServer;
    [NativeTypeName("const char *")]
    public sbyte* bindAddress;
    public rtcCertificateType certificateType;
    public rtcTransportPolicy iceTransportPolicy;
    [NativeTypeName("_Bool")]
    public byte enableIceTcp;
    [NativeTypeName("_Bool")]
    public byte enableIceUdpMux;
    [NativeTypeName("_Bool")]
    public byte disableAutoNegotiation;
    [NativeTypeName("_Bool")]
    public byte forceMediaTransport;
    [NativeTypeName("uint16_t")]
    public ushort portRangeBegin;
    [NativeTypeName("uint16_t")]
    public ushort portRangeEnd;
    public int mtu;
    public int maxMessageSize;
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal partial struct rtcReliability
{
    [NativeTypeName("_Bool")]
    public byte unordered;
    [NativeTypeName("_Bool")]
    public byte unreliable;
    [NativeTypeName("unsigned int")]
    public uint maxPacketLifeTime;
    [NativeTypeName("unsigned int")]
    public uint maxRetransmits;
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal unsafe partial struct rtcDataChannelInit
{
    public rtcReliability reliability;
    [NativeTypeName("const char *")]
    public sbyte* protocol;
    [NativeTypeName("_Bool")]
    public byte negotiated;
    [NativeTypeName("_Bool")]
    public byte manualStream;
    [NativeTypeName("uint16_t")]
    public ushort stream;
}
[GeneratedCode("ClangSharp", "21.1.8.4")]
internal static unsafe partial class Rtc
{
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void rtcSetUserPointer(int id, void* ptr);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcCreatePeerConnection([NativeTypeName("const rtcConfiguration *")] rtcConfiguration* config);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcDeletePeerConnection(int pc);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSetStateChangeCallback(int pc, [NativeTypeName("rtcStateChangeCallbackFunc")] delegate* unmanaged[Cdecl]<int, rtcState, void*, void> cb);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSetGatheringStateChangeCallback(int pc, [NativeTypeName("rtcGatheringStateCallbackFunc")] delegate* unmanaged[Cdecl]<int, rtcGatheringState, void*, void> cb);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSetRemoteDescription(int pc, [NativeTypeName("const char *")] sbyte* sdp, [NativeTypeName("const char *")] sbyte* type);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcGetLocalDescription(int pc, [NativeTypeName("char *")] sbyte* buffer, int size);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcGetRemoteAddress(int pc, [NativeTypeName("char *")] sbyte* buffer, int size);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcGetSelectedCandidatePair(int pc, [NativeTypeName("char *")] sbyte* local, int localSize, [NativeTypeName("char *")] sbyte* remote, int remoteSize);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSetOpenCallback(int id, [NativeTypeName("rtcOpenCallbackFunc")] delegate* unmanaged[Cdecl]<int, void*, void> cb);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSetMessageCallback(int id, [NativeTypeName("rtcMessageCallbackFunc")] delegate* unmanaged[Cdecl]<int, sbyte*, int, void*, void> cb);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcSendMessage(int id, [NativeTypeName("const char *")] sbyte* data, int size);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("_Bool")]
    public static extern byte rtcIsOpen(int id);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcGetBufferedAmount(int id);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcCreateDataChannelEx(int pc, [NativeTypeName("const char *")] sbyte* label, [NativeTypeName("const rtcDataChannelInit *")] rtcDataChannelInit* init);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcDeleteDataChannel(int dc);
    [DllImport("datachannel", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int rtcGetDataChannelReliability(int dc, rtcReliability* reliability);
}
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
[Conditional("DEBUG")]
internal sealed partial class NativeTypeNameAttribute : Attribute
{
    private readonly string _name;
    public NativeTypeNameAttribute(string name)
    {
        _name = name;
    }
    public string Name => _name;
}
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
[Conditional("DEBUG")]
internal sealed partial class NativeAnnotationAttribute : Attribute
{
    private readonly string _annotation;
    public NativeAnnotationAttribute(string annotation)
    {
        _annotation = annotation;
    }
    public string Annotation => _annotation;
}
