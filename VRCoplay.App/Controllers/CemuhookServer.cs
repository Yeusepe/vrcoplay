// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace VRCoplay;
internal sealed class CemuhookServer : IDisposable
{
    internal const int DefaultPort = 26760;
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly bool[] _connected = new bool[4];
    private readonly uint[] _sequences = new uint[4];
    private readonly ulong[] _timestamps = new ulong[4];
    private readonly ulong[] _sourceTimestamps = new ulong[4];
    private readonly Dictionary<IPEndPoint, Subscription> _clients = new();
    private readonly uint _id = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    private readonly int _hand;
    private readonly Task _receive;
    private readonly TaskCompletionSource<Exception> _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    internal int Port { get; }
    internal Task<Exception> Fault => _fault.Task;
    internal static ulong Timestamp => (ulong)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
    private sealed class Subscription(uint id)
    {
        internal readonly uint Id = id;
        internal readonly long[] Slots = new long[4];
        internal bool Fresh(int slot, long now) => Slots[slot] != 0 && Stopwatch.GetElapsedTime(Slots[slot], now).TotalSeconds < 5;
    }
    internal CemuhookServer(int port, int hand)
    {
        _hand = hand;
        try
        {
            if ((uint)port > 65535 || (uint)hand > 1) throw new ArgumentOutOfRangeException(nameof(port));
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
            _receive = ReceiveAsync();
        }
        catch (Exception error)
        {
            _socket.Dispose(); _stop.Dispose();
            throw new IOException($"Could not start Cemuhook on 127.0.0.1:{port}. Choose another Cemuhook port in Controls or close the other motion server.", error);
        }
    }
    internal void SetConnected(int slot, bool connected)
    {
        lock (_gate)
        {
            if (_connected[slot] && !connected)
                Update(slot, DualShockState.Neutral, ControllerMotion.Neutral, 0);
            _connected[slot] = connected;
        }
    }
    internal void Update(int slot, in DualShockState controls, in ControllerMotion motion, ulong timestamp)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_connected[slot]) return;
            var previousSource = _sourceTimestamps[slot];
            var interval = timestamp > previousSource ? timestamp - previousSource : 0;
            _timestamps[slot] = previousSource != 0 && interval is > 0 and <= 100_000
                ? _timestamps[slot] + interval : Math.Max(Timestamp, _timestamps[slot] + 1);
            _sourceTimestamps[slot] = timestamp;
            var sequence = ++_sequences[slot];
            var now = Stopwatch.GetTimestamp();
            Prune(now);
            if (_clients.Count == 0) return;
            Span<byte> packet = stackalloc byte[100];
            CemuhookProtocol.Data(packet, _id, slot, _hand, sequence, _timestamps[slot], controls, motion);
            foreach (var client in _clients)
                if (client.Value.Fresh(slot, now)) Send(packet, client.Key);
        }
    }
    private async Task ReceiveAsync()
    {
        var buffer = new byte[1024];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), _stop.Token).ConfigureAwait(false);
                    if (result.RemoteEndPoint is IPEndPoint peer && IPAddress.IsLoopback(peer.Address))
                        Receive(buffer.AsSpan(0, result.ReceivedBytes), peer);
                }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or
                    SocketError.ConnectionRefused or SocketError.MessageSize) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (!_stop.IsCancellationRequested) _fault.TrySetResult(error); }
    }
    private void Receive(ReadOnlySpan<byte> packet, IPEndPoint peer)
    {
        if (!CemuhookProtocol.TryRequest(packet, out var type, out var id)) return;
        lock (_gate)
        {
            if (_disposed != 0) return;
            switch (type)
            {
                case CemuhookProtocol.VersionMessage when packet.Length == 20:
                {
                    Span<byte> reply = stackalloc byte[22];
                    CemuhookProtocol.Header(reply, type, _id);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[20..], CemuhookProtocol.Version);
                    CemuhookProtocol.Finish(reply);
                    Send(reply, peer);
                    break;
                }
                case CemuhookProtocol.PortsMessage when packet.Length >= 24:
                {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(packet[20..]);
                    if (count is < 1 or > 4 || packet.Length < 24 + count || packet.Length > 32) return;
                    for (var i = 0; i < count; i++) if (packet[24 + i] >= 4) return;
                    Span<byte> reply = stackalloc byte[32];
                    for (var i = 0; i < count; i++)
                    {
                        var slot = packet[24 + i];
                        CemuhookProtocol.Header(reply, type, _id);
                        CemuhookProtocol.Port(reply[20..], slot, _hand, _connected[slot]);
                        CemuhookProtocol.Finish(reply);
                        Send(reply, peer);
                    }
                    break;
                }
                case CemuhookProtocol.DataMessage when packet.Length == 28:
                {
                    var flags = packet[20];
                    if (flags > 3 || ((flags & 1) != 0 && packet[21] >= 4)) return;
                    var now = Stopwatch.GetTimestamp();
                    Prune(now);
                    if (!_clients.TryGetValue(peer, out var client) || client.Id != id)
                    {
                        if (_clients.Count >= 32) return;
                        _clients[peer] = client = new(id);
                    }
                    Span<byte> mac = stackalloc byte[6];
                    for (var slot = 0; slot < 4; slot++)
                    {
                        CemuhookProtocol.Mac(mac, slot, _hand);
                        if (flags == 0 || ((flags & 1) != 0 && slot == packet[21]) ||
                            ((flags & 2) != 0 && mac.SequenceEqual(packet[22..28]))) client.Slots[slot] = now;
                    }
                    break;
                }
            }
        }
    }
    private void Prune(long now)
    {
        foreach (var peer in _clients.Where(pair => !Enumerable.Range(0, 4).Any(slot => pair.Value.Fresh(slot, now)))
            .Select(pair => pair.Key).ToArray()) _clients.Remove(peer);
    }
    private void Send(ReadOnlySpan<byte> packet, IPEndPoint peer)
    {
        try { _socket.SendTo(packet, SocketFlags.None, peer); }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or
            SocketError.ConnectionRefused or SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        lock (_gate) { _clients.Clear(); _socket.Dispose(); }
        _receive.GetAwaiter().GetResult();
        _stop.Dispose();
        _fault.TrySetCanceled();
    }
}
