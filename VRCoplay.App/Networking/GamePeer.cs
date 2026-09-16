// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using VRCoplay.NativeRtc;
using static VRCoplay.NativeRtc.Rtc;
namespace VRCoplay;
internal sealed class GamePeer : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle<GamePeer> _root;
    private int _connection = -1;
    private readonly int[] _channels = [-1, -1];
    private Task? _disposal;
    public event Action<ReadOnlySpan<byte>>? SessionMessage;
    public event Action<ReadOnlySpan<byte>>? ControllerReport;
    public Task Ready => _ready.Task;
    public Task<Exception> Closed => _closed.Task;
    public unsafe GamePeer(string? bindAddress = null)
    {
        try
        {
            var stunServer = Environment.GetEnvironmentVariable("VRCOPLAY_STUN_SERVER") ?? "stun:stun.l.google.com:19302";
            if (stunServer.Length > 2048 || stunServer.Any(char.IsControl)
                || stunServer.Length != 0 && !stunServer.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("VRCOPLAY_STUN_SERVER must be a stun: URI or empty to disable STUN.");
            fixed (
                byte* stun = Encoding.UTF8.GetBytes(stunServer + '\0'),
                    address = bindAddress is null ? null : Encoding.UTF8.GetBytes(bindAddress + '\0')
            )
            {
                var server = (sbyte*)stun;
                var config = new rtcConfiguration
                {
                    iceServers = stunServer.Length == 0 ? null : &server,
                    iceServersCount = stunServer.Length == 0 ? 0 : 1,
                    bindAddress = (sbyte*)address,
                    maxMessageSize = 16384,
                };
                _connection = Check(rtcCreatePeerConnection(&config));
            }
            _root = new(this);
            rtcSetUserPointer(_connection, (void*)GCHandle<GamePeer>.ToIntPtr(_root));
            Check(rtcSetStateChangeCallback(_connection, &StateChanged));
            Check(rtcSetGatheringStateChangeCallback(_connection, &GatheringChanged));
        }
        catch
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
    private unsafe void CreateChannel(string label, ushort stream)
    {
        var config = new rtcDataChannelInit
        {
            negotiated = 1,
            manualStream = 1,
            stream = stream,
        };
        if (stream == 1)
            config.reliability = new()
            {
                unordered = 1,
                unreliable = 1,
                maxPacketLifeTime = 50,
            };
        var channel = _channels[stream] = Check(rtcCreateDataChannelEx(_connection, label, in config));
        Check(rtcSetOpenCallback(channel, &Opened));
        Check(rtcSetMessageCallback(channel, &Received));
    }
    public async Task<string> DescribeAsync(string? offer, CancellationToken stop)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (offer is not null)
                SetRemote(offer, "offer");
            CreateChannel("session", 0);
            CreateChannel("controller", 1);
        }
        await _gathered.Task.WaitAsync(stop);
        return ReadText(rtcGetLocalDescription);
    }
    public void AcceptAnswer(string answer) => SetRemote(answer, "answer");
    private void SetRemote(string remote, string type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            Check(rtcSetRemoteDescription(_connection, remote, type));
        }
    }
    public string RemoteAddress => ParseNativeEndpoint(ReadText(rtcGetRemoteAddress))?.ToString() ?? "";
    internal static IPEndPoint? ParseNativeEndpoint(string address)
    {
        var separator = address.LastIndexOf(':');
        return separator > 0
            && IPAddress.TryParse(address.AsSpan(0, separator), out var ip)
            && int.TryParse(address.AsSpan(separator + 1), out var port)
            && port is > 0 and <= 65535
                ? new(ip, port)
                : null;
    }
    private unsafe string ReadText(Func<int, nint, int, int> read)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            var buffer = stackalloc sbyte[32768];
            return Marshal.PtrToStringUTF8((nint)buffer, Check(read(_connection, (nint)buffer, 32768)) - 1)!;
        }
    }
    public void SendSession(ReadOnlySpan<byte> message)
    {
        if (!Send(_channels[0], message, 65536))
            throw new IOException("The peer is disconnected or is not consuming session updates.");
    }
    public bool SendController(ReadOnlySpan<byte> report) => Send(_channels[1], report, 0);
    private unsafe bool Send(int channel, ReadOnlySpan<byte> data, int bufferLimit)
    {
        lock (_gate)
        {
            if (_disposal is not null || rtcIsOpen(channel) == 0 || rtcGetBufferedAmount(channel) > bufferLimit)
                return false;
            fixed (byte* bytes = data)
                Check(rtcSendMessage(channel, (sbyte*)bytes, data.Length));
            return true;
        }
    }
    private void Fail(Exception error)
    {
        _ready.TrySetException(error);
        _gathered.TrySetException(error);
        _closed.TrySetResult(error);
    }
    private static unsafe GamePeer Owner(void* context) => GCHandle<GamePeer>.FromIntPtr((nint)context).Target;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void StateChanged(int id, rtcState state, void* context)
    {
        if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
            Owner(context).Fail(new IOException("The direct peer connection closed. Rejoin to request control again."));
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void GatheringChanged(int id, rtcGatheringState state, void* context)
    {
        if (state == rtcGatheringState.RTC_GATHERING_COMPLETE)
            Owner(context)._gathered.TrySetResult();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Opened(int id, void* context)
    {
        var peer = Owner(context);
        if (peer._channels.All(channel => rtcIsOpen(channel) != 0))
            peer._ready.TrySetResult();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Received(int id, sbyte* bytes, int size, void* context)
    {
        var peer = Owner(context);
        try
        {
            if (Volatile.Read(ref peer._disposal) is not null)
                return;
            if (size <= 0 || size > 16384)
                throw new InvalidDataException("Invalid peer message.");
            var message = new ReadOnlySpan<byte>(bytes, size);
            if (id == peer._channels[1])
                peer.ControllerReport?.Invoke(message);
            else
                peer.SessionMessage?.Invoke(message);
        }
        catch (Exception error)
        {
            peer.Fail(error);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new(
                _disposal ??= Task.Run(() =>
                {
                    Fail(new ObjectDisposedException(nameof(GamePeer)));
                    foreach (var channel in _channels)
                        if (channel >= 0)
                            rtcDeleteDataChannel(channel);
                    if (_connection >= 0)
                        rtcDeletePeerConnection(_connection);
                    _root.Dispose();
                })
            );
    }
    private static int Check(int result) =>
        result >= 0 ? result : throw new IOException($"libdatachannel error {result}.");
}
