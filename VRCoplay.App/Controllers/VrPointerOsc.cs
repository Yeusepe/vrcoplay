// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using FastOSC;
using VRC.OSCQuery;
namespace VRCoplay;
internal sealed partial class VrPointerOsc : IDisposable
{
    private const string Change = "/avatar/change", X = "/avatar/parameters/VRCoplay/PointerX", Y = "/avatar/parameters/VRCoplay/PointerY", Hit = "/avatar/parameters/VRCoplay/PointerHit", Lock = "/avatar/parameters/VRCoplay/PointerLock", Ghost = "/avatar/parameters/VRCoplay/PointerGhost", Enabled = "/avatar/parameters/VRCoplay/PointerEnabled", Ray = "/avatar/parameters/VRCoplay/Ray_Distance";
    private static readonly string[] Paths = [X, Y, Hit, Lock, Ghost];
    private static readonly string[] Statuses = ["Avatar asset not detected", "Point 1 of 4", "Point 1 of 4", "Point 2 of 4", "Point 3 of 4", "Point 4 of 4", "Ready"];
    private const string VideoCalibration = "/avatar/parameters/VRCoplay/VideoCalibration";
    private const string RayHit = "/avatar/parameters/VRCoplay/Ray_Hit";
    private const string Tick = "/avatar/parameters/VRCoplay/PointerTick";
    private const string Scale = "/avatar/parameters/VRCoplay/PointerScale";
    private const string Ready = "/avatar/parameters/VRCoplay/PointerReady", OnScreen = "/avatar/parameters/VRCoplay/PointerOnScreen";
    private readonly object _gate;
    private readonly Action<string> _status;
    private readonly Action<bool>? _calibrationChanged;
    private readonly Action<PointerCalibrationProgress>? _progressChanged;
    private PointerCalibrationProgress _progress;
    private readonly Vector2[] _corners = new Vector2[4];
    private readonly PointerSamples _samples = new();
    private PointerCalibration? _mapping;
    private bool _calibrated;
    private readonly OSCReceiver _receiver;
    private readonly OSCQueryService _query;
    private readonly Timer _refresh;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _fallbackPort;
    private UdpClient? _fallback;
    private long _queryTickAt;
    private readonly HashSet<OSCQueryServiceProfile> _discovering = [];
    private Socket? _sender;
    private OSCQueryServiceProfile? _vrchat;
    private IPEndPoint? _destination;
    private string? _avatar, _expectedAvatar, _lastStatus;
    private string _unavailable = "Looking for VRChat OSC";
    private Vector2 _point;
    private float _measurementScale = 1;
    private long _sampleAt, _lockAt, _generation, _verifiedAt, _avatarChangedAt;
    private bool _asset, _hit, _rayHit, _trigger, _enabled, _hasToggle, _onScreen;
    private int _stage, _pulse, _disposed, _axes;
    internal readonly record struct Touch(ushort X, ushort Y, bool Active);
    internal VrPointerOsc(Action<string> status, bool enabled = true, Action<bool>? calibrationChanged = null, IDiscovery? discovery = null, object? gate = null, Action<PointerCalibrationProgress>? progressChanged = null, int fallbackPort = 9001, string? alignmentProfilePath = null)
    {
        _gate = gate ?? new();
        _alignmentProfilePath = alignmentProfilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "pointer-alignment.json");
        _alignmentProfiles = LoadAlignmentProfiles(_alignmentProfilePath);
        _alignmentReferencePath = Path.ChangeExtension(_alignmentProfilePath, ".references.json");
        _alignmentReferences = LoadAlignmentReferences(_alignmentReferencePath);
        _status = status;
        _calibrationChanged = calibrationChanged;
        _progressChanged = progressChanged;
        _enabled = enabled;
        _fallbackPort = fallbackPort;
        var udp = VRC.OSCQuery.Extensions.GetAvailableUdpPort();
        var tcp = VRC.OSCQuery.Extensions.GetAvailableTcpPort();
        _receiver = new(65535);
        _receiver.OnPacketReceived += packet =>
        {
            try
            {
                lock (_gate)
                    foreach (var message in Messages(packet))
                    {
                        if (message.Address == Tick && message.Arguments.FirstOrDefault() is float tick && float.IsFinite(tick))
                            _queryTickAt = Stopwatch.GetTimestamp();
                        Receive(message);
                    }
            }
            catch (Exception error) { Debug.WriteLine($"Pointer OSC callback: {error.Message}"); }
            return Task.CompletedTask;
        };
        _receiver.Connect(new(IPAddress.Loopback, udp));
        var builder = new OSCQueryServiceBuilder()
            .WithServiceName($"VRCoplay-Pointer-{Environment.ProcessId}-{Guid.NewGuid():N}")
            .WithHostIP(IPAddress.Loopback).WithOscIP(IPAddress.Loopback)
            .WithTcpPort(tcp).WithUdpPort(udp);
        _query = builder.Build();
        _refresh = new(_ => Refresh(), null, Timeout.Infinite, 2000);
        try
        {
            _query.AddEndpoint<string>(Change, Attributes.AccessValues.WriteOnly);
            foreach (var path in new[] { X, Y, Ray, Tick, Scale }) _query.AddEndpoint<float>(path, Attributes.AccessValues.WriteOnly);
            foreach (var path in new[] { Hit, Lock, Ghost, Enabled, RayHit }) _query.AddEndpoint<bool>(path, Attributes.AccessValues.WriteOnly);
            AdvertiseAlignment();
            _query.OnOscQueryServiceAdded += ServiceAdded;
            builder.WithDiscovery(discovery ?? new MeaModDiscovery()).StartHttpServer().AdvertiseOSCQuery().AdvertiseOSC();
            lock (_gate) SetStatusLocked();
            _refresh.Change(0, 2000);
        }
        catch { Dispose(); throw; }
    }
    private void ServiceAdded(OSCQueryServiceProfile? profile) => _ = DiscoverAsync(profile);
    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            try { _query.RefreshServices(); }
            catch (Exception error) { Debug.WriteLine($"Pointer service refresh: {error.Message}"); }
            var services = _query.GetOSCQueryServices();
            lock (_gate)
            {
                if (_vrchat is not null) services.Add(_vrchat);
                if (_asset && Stopwatch.GetElapsedTime(_verifiedAt).TotalSeconds > 6)
                    ClearAvatarLocked("VRChat OSC unavailable; reconnecting");
                RefreshFallbackLocked();
            }
            foreach (var profile in services) _ = DiscoverAsync(profile);
        }
        catch (Exception error) { Debug.WriteLine($"Pointer discovery: {error.Message}"); }
    }
    private async Task DiscoverAsync(OSCQueryServiceProfile? profile)
    {
        if (profile is null) return;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed != 0 || profile.name == _query.ServerName || !_discovering.Add(profile)) return;
            generation = _generation;
            token = _stop.Token;
        }
        Socket? sender = null;
        try
        {
            var tree = await VRC.OSCQuery.Extensions.GetOSCTree(profile.address, profile.port).WaitAsync(token).ConfigureAwait(false);
            if (tree is null) throw new IOException("OSCQuery tree unavailable.");
            if (!Supports(tree, Change, Attributes.AccessValues.ReadOnly) && !SupportsAsset(tree)) return;
            var host = await VRC.OSCQuery.Extensions.GetHostInfo(profile.address, profile.port).WaitAsync(token).ConfigureAwait(false);
            if (host is null || host.oscPort is < 1 or > 65535 || host.oscTransport != HostInfo.Keys.OSC_TRANSPORT_UDP)
                throw new IOException("OSCQuery did not provide a UDP destination.");
            var address = IPAddress.TryParse(host.oscIP, out var parsed) && !IPAddress.Any.Equals(parsed) ? parsed : profile.address;
            var destination = new IPEndPoint(address, host.oscPort);
            var avatar = tree.GetNodeWithPath(Change)?.Value?.FirstOrDefault() as string;
            var hasAsset = SupportsAsset(tree);
            lock (_gate)
            {
                if (!AcceptQueryLocked(profile, generation, avatar)) return;
                if (hasAsset && (_sender is null || !destination.Equals(_destination)))
                {
                    sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sender.Connect(destination);
                }
                if (_disposed != 0) return;
                var changed = _vrchat is not null && (!_vrchat.Equals(profile) ||
                    (_avatar is not null && avatar is not null && avatar != _avatar) ||
                    (_destination is not null && !destination.Equals(_destination)));
                if (changed) ClearAvatarLocked("Checking avatar pointer", disconnect: false);
                _vrchat = profile;
                _avatar = avatar ?? _avatar;
                _expectedAvatar = null;
                _verifiedAt = Stopwatch.GetTimestamp();
                if (!hasAsset)
                {
                    ClearAvatarLocked(tree.GetNodeWithPath(X) is not null &&
                        (!Supports(tree, VideoCalibration, Attributes.AccessValues.WriteOnly) || !Supports(tree, Tick, Attributes.AccessValues.ReadOnly))
                        ? "Update the avatar pointer for smooth calibration"
                        : Paths.Any(path => tree.GetNodeWithPath(path) is not null)
                        ? "Avatar pointer OSC parameters incomplete" : "Avatar asset not detected");
                    return;
                }
                if (sender is not null)
                {
                    DisconnectLocked();
                    _sender = sender;
                    sender = null;
                }
                _destination = destination;
                if (_asset) return;
                _asset = true;
                RefreshFallbackLocked();
                _measurementScale = Supports(tree, Scale, Attributes.AccessValues.ReadOnly) ? 0 : 1;
                _hasToggle = Supports(tree, Enabled, Attributes.AccessValues.WriteOnly);
                SendLocked(VideoCalibration, true);
                SendLocked(Lock, false);
                SendLocked(Ghost, false);
                ResetLocked();
                DiscoverAlignmentLocked(tree);
                if (!_asset) return;
                foreach (var path in new[] { Scale, X, Y, Hit, RayHit, Tick })
                    if (tree.GetNodeWithPath(path)?.Value?.FirstOrDefault() is { } value)
                        Receive(new OSCMessage(path, path == Hit || path == RayHit ? Convert.ToBoolean(value) : (object)Convert.ToSingle(value)));
                if (_hasToggle) SendLocked(Enabled, _enabled);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Debug.WriteLine($"Pointer OSCQuery: {error.Message}"); }
        finally
        {
            sender?.Dispose();
            lock (_gate) _discovering.Remove(profile);
        }
    }
    private bool AcceptQueryLocked(OSCQueryServiceProfile profile, long generation, string? avatar)
    {
        if (_disposed != 0 || generation != _generation) return false;
        if (_asset && _vrchat is not null && !_vrchat.Equals(profile)) return false;
        if (_expectedAvatar is null) return true;
        var elapsed = Stopwatch.GetElapsedTime(_avatarChangedAt).TotalSeconds;
        return elapsed >= .25 && (avatar is null || avatar == _expectedAvatar || elapsed >= 5);
    }
    private static bool Supports(OSCQueryRootNode tree, string path, Attributes.AccessValues access)
        => tree.GetNodeWithPath(path) is { } node && (node.Access & access) == access;
    private static bool SupportsAsset(OSCQueryRootNode tree)
        => new[] { X, Y, Hit, RayHit, Tick }.All(path => Supports(tree, path, Attributes.AccessValues.ReadOnly))
           && Supports(tree, Lock, Attributes.AccessValues.ReadWrite)
           && Supports(tree, Ghost, Attributes.AccessValues.WriteOnly)
           && Supports(tree, VideoCalibration, Attributes.AccessValues.WriteOnly);
    private bool QueryLiveLocked() => _queryTickAt != 0 && Stopwatch.GetElapsedTime(_queryTickAt).TotalSeconds < 2;
    private void RefreshFallbackLocked()
    {
        if (_disposed != 0 || !_enabled || !_asset || _destination is null ||
            !IPAddress.IsLoopback(_destination.Address) || QueryLiveLocked())
        {
            StopFallbackLocked();
            return;
        }
        if (_fallback is not null || _fallbackPort == 0) return;
        var receiver = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            receiver.ExclusiveAddressUse = true;
            receiver.Client.Bind(new IPEndPoint(IPAddress.Loopback, _fallbackPort));
            _fallback = receiver;
            _ = ReceiveFallbackAsync(receiver);
            Debug.WriteLine("Pointer OSC: listening on the default local output while discovery recovers.");
        }
        catch (SocketException error)
        {
            receiver.Dispose();
            Debug.WriteLine($"Pointer OSC default output unavailable: {error.SocketErrorCode}");
        }
    }
    private async Task ReceiveFallbackAsync(UdpClient receiver)
    {
        try
        {
            while (true)
            {
                var packet = await receiver.ReceiveAsync().ConfigureAwait(false);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;
                try
                {
                    var decoded = OSCDecoder.Decode(packet.Buffer);
                    if (decoded is null) continue;
                    lock (_gate)
                    {
                        if (_disposed != 0 || !ReferenceEquals(_fallback, receiver)) return;
                        if (QueryLiveLocked()) continue;
                        foreach (var message in Messages(decoded)) Receive(message);
                    }
                }
                catch (Exception error) { Debug.WriteLine($"Pointer OSC default packet: {error.Message}"); }
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException error) { Debug.WriteLine($"Pointer OSC default receiver: {error.SocketErrorCode}"); }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_fallback, receiver)) StopFallbackLocked();
        }
    }
    private void StopFallbackLocked()
    {
        var receiver = _fallback;
        _fallback = null;
        receiver?.Dispose();
    }
    private void Receive(OSCMessage message)
    {
        var value = message.Arguments.FirstOrDefault();
        lock (_gate)
        {
            if (_disposed != 0) return;
            var now = Stopwatch.GetTimestamp();
            if (message.Address == Change && value is string { Length: > 0 } avatar)
            {
                _expectedAvatar = avatar;
                _avatarChangedAt = now;
                ClearAvatarLocked("Checking avatar pointer");
                _refresh.Change(250, 2000);
            }
            else if (!_asset) return;
            else if (ReceiveAlignmentLocked(message)) return;
            else if (message.Address == Enabled && _asset && value is bool enabled && enabled != _enabled) SetEnabled(enabled);
            else if (message.Address == Tick && value is float tick && float.IsFinite(tick)) _sampleAt = now;
            else if (message.Address == Scale && value is float scale)
            {
                scale = float.IsFinite(scale) && scale is > 0 and <= 1 ? scale : 0;
                if (scale != _measurementScale)
                {
                    _measurementScale = scale;
                    _samples.Clear();
                    if (_stage > 1) { SendLocked(Lock, false); ResetLocked(); }
                }
            }
            else if (message.Address == X && value is float x && float.IsFinite(x)) { _point.X = x; _axes |= 1; }
            else if (message.Address == Y && value is float y && float.IsFinite(y)) { _point.Y = y; _axes |= 2; }
            else if (message.Address == Hit && value is bool hit) { _hit = hit; if (!hit) _samples.Clear(); }
            else if (message.Address == RayHit && value is bool rayHit) { _rayHit = rayHit; if (!rayHit) _samples.Clear(); }
            else if (message.Address == Lock && value is bool locked)
            {
                if (locked && _stage == 2) { _samples.Clear(); StepLocked(3, 0); }
                else if (!locked && _stage > 2) { _pulse = 3; ResetLocked(); }
            }
        }
    }
    internal bool Trigger(float value, int displayedTarget = 0)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_enabled || !_asset) return false;
            if (_alignmentPhase != 0) { AlignmentTriggerLocked(value); _trigger = true; return true; }
            if (_alignmentCompletedAt != 0) { _trigger = true; return true; }
            if (_stage == 6) { if (value < .5f) _trigger = false; return _trigger; }
            if (value < .5f) _trigger = false;
            if (_trigger || !float.IsFinite(value) || value < .75f || _stage == 2) return true;
            _trigger = true;
            if (displayedTarget != 0 && displayedTarget != (_stage <= 2 ? 1 : _stage - 1)) return true;
            var issue = SampleLocked(out var sample);
            if (issue != PointerCalibrationIssue.None)
            { _pulse = 3; PublishProgressLocked(true, issue); return true; }
            switch (_stage)
            {
                case 1:
                    _corners[0] = sample; _stage = 2; _lockAt = Stopwatch.GetTimestamp(); SendLocked(Lock, true); break;
                case 3:
                case 4:
                case 5:
                    if (_corners.Take(_stage - 2).Any(p => Vector2.DistanceSquared(p, sample) < .000025f))
                    { _pulse = 3; PublishProgressLocked(true, PointerCalibrationIssue.Duplicate); return true; }
                    _corners[_stage - 2] = sample;
                    _samples.Clear();
                    if (_stage < 5) StepLocked(_stage + 1);
                    else if (PointerCalibration.TryCreate(_corners, out _mapping)) StepLocked(6, 2);
                    else { SendLocked(Lock, false); _pulse = 3; ResetLocked(); PublishProgressLocked(true, PointerCalibrationIssue.InvalidGeometry); }
                    return true;
            }
            _pulse = 1;
            return true;
        }
    }
    internal Touch Read(out int pulse)
    {
        lock (_gate)
        {
            AdvanceAlignmentCompletionLocked();
            if (_alignmentPhase == 0 && _alignmentCompletedAt == 0 && _enabled && _asset && _stage is 1 or 3 or 4 or 5 && _hit && FreshLocked())
                _samples.Add(MeasurementLocked(), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            else _samples.Clear();
            if (_progress.Retry && _progress.Issue is not (PointerCalibrationIssue.Duplicate or PointerCalibrationIssue.InvalidGeometry) &&
                SampleLocked(out _) == PointerCalibrationIssue.None) PublishProgressLocked();
            if (_stage == 2 && Stopwatch.GetElapsedTime(_lockAt).TotalSeconds > 1)
            {
                SendLocked(Ghost, false); SendLocked(Lock, false); _pulse = 3; ResetLocked();
            }
            pulse = _pulse;
            _pulse = 0;
            if (_disposed != 0 || !_enabled || _alignmentPhase != 0 || _stage != 6 || _mapping is null || !_hit || !FreshLocked()) { SetAimLocked(false); return default; }
            var uv = _mapping.Map(MeasurementLocked());
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) { SetAimLocked(false); return default; }
            var inside = Vector2.GreaterThanOrEqualAll(uv, Vector2.Zero) && Vector2.LessThanOrEqualAll(uv, Vector2.One);
            SetAimLocked(inside);
            return new(
                (ushort)MathF.Round(Math.Clamp(uv.X, 0, 1) * 1919),
                (ushort)MathF.Round(Math.Clamp(uv.Y, 0, 1) * 941),
                inside
            );
        }
    }
    internal void Recalibrate()
    {
        lock (_gate) if (_disposed == 0 && _asset) { SendLocked(Ghost, false); SendLocked(Lock, false); ResetLocked(); }
    }
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed != 0 || _enabled == enabled) return;
            _enabled = enabled;
            if (!enabled) CancelAlignmentLocked();
            RefreshFallbackLocked();
            if (_asset) { SendLocked(Ghost, false); SendLocked(Lock, false); if (_hasToggle) SendLocked(Enabled, enabled); }
            ResetLocked();
            if (enabled && _asset && _hasAlignment && !_alignmentDone) SetAlignmentStateLocked(1, "Move your controller into the blue outline.");
        }
    }
    private Vector2 MeasurementLocked() => (_point - new Vector2(.5f)) / _measurementScale + new Vector2(.5f);
    private bool SignalLocked() => _measurementScale > 0 && _axes == 3 && _sampleAt != 0 && Stopwatch.GetElapsedTime(_sampleAt).TotalMilliseconds <= 150;
    private bool FreshLocked() => _rayHit && SignalLocked();
    private PointerCalibrationIssue SampleLocked(out Vector2 sample)
    {
        sample = default;
        if (!SignalLocked()) return PointerCalibrationIssue.NoSignal;
        if (!_rayHit) return PointerCalibrationIssue.Miss;
        if (!_hit || _point.X is <= .001f or >= .999f || _point.Y is <= .001f or >= .999f) return PointerCalibrationIssue.OutsidePlane;
        return _samples.TryRead(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, out sample)
            ? PointerCalibrationIssue.None : PointerCalibrationIssue.Moving;
    }
    private void SetAimLocked(bool inside) { if (_onScreen != inside) { _onScreen = inside; SendLocked(OnScreen, inside); } }
    private void StepLocked(int stage, int pulse = 1) { if (!_asset) return; _stage = stage; _pulse = Math.Max(_pulse, pulse); SendLocked(Ready, stage == 6); SetStatusLocked(); }
    private void ResetLocked() { _stage = _asset ? 1 : 0; _mapping = null; _samples.Clear(); Array.Clear(_corners); _trigger = true; _sampleAt = _lockAt = 0; SendLocked(Ready, false); SetAimLocked(false); SetStatusLocked(); }
    private void PublishProgressLocked(bool retry = false, PointerCalibrationIssue issue = PointerCalibrationIssue.None)
    {
        var next = new PointerCalibrationProgress(!_enabled || !_asset || _alignmentPhase != 0 || _alignmentCompletedAt != 0 ? 0 : _stage <= 2 ? 1 : _stage - 1, retry, issue);
        if (_progress == next) return;
        _progress = next;
        _progressChanged?.Invoke(next);
    }
    private void SetStatusLocked()
    {
        PublishProgressLocked();
        var calibrated = _enabled && _asset && _alignmentPhase == 0 && _stage == 6;
        if (_calibrated != calibrated)
        {
            _calibrated = calibrated;
            _calibrationChanged?.Invoke(calibrated);
        }
        var status = !_enabled ? "Off" : _stage == 0 ? _unavailable : _alignmentPhase != 0 || _alignmentCompletedAt != 0 ? _alignmentMessage : Statuses[_stage];
        if (_lastStatus != status) { _lastStatus = status; _status(status); }
    }
    private void SendLocked(string path, bool value) { try { _sender?.Send(OSCEncoder.Encode(new OSCMessage(path, value))); } catch { LoseLocked(); } }
    private void DisconnectLocked() { _sender?.Dispose(); _sender = null; _destination = null; }
    private void ClearAvatarLocked(string status, bool disconnect = true)
    {
        CancelAlignmentLocked();
        _hasAlignment = false;
        _alignmentProfileKey = null;
        StopFallbackLocked();
        _queryTickAt = 0;
        _generation++;
        if (disconnect) DisconnectLocked();
        _asset = _hit = _rayHit = _hasToggle = false;
        _axes = _pulse = 0;
        _point = default;
        _unavailable = status;
        ResetLocked();
    }
    private void LoseLocked()
    {
        _vrchat = null; _avatar = _expectedAvatar = null; _verifiedAt = 0;
        ClearAvatarLocked("Looking for VRChat OSC");
    }
    private static IEnumerable<OSCMessage> Messages(IOSCPacket packet) => packet switch
    {
        OSCMessage message => [message],
        OSCBundle bundle => bundle.Packets.SelectMany(Messages),
        _ => [],
    };
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _refresh.Dispose();
        _stop.Cancel();
        _query.OnOscQueryServiceAdded -= ServiceAdded;
        lock (_gate) { SendLocked(Ghost, false); SendLocked(Lock, false); SendLocked(VideoCalibration, false); if (_hasToggle) SendLocked(Enabled, false); LoseLocked(); }
        try { _query.Dispose(); } catch { }
        try { Task.Run(_receiver.DisconnectAsync).GetAwaiter().GetResult(); } catch { }
        _stop.Dispose();
    }
}
