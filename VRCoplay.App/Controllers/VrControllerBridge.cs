// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Valve.VR;
namespace VRCoplay;
internal sealed partial class VrControllerBridge : IDisposable
{
    private static readonly uint SetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>(),
        AnalogSize = (uint)Marshal.SizeOf<InputAnalogActionData_t>(),
        DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
    private static readonly string[] AnalogActions = ["left_stick", "right_stick", "left_trigger", "right_trigger"];
    private static readonly (string Name, ushort Button)[] DigitalActions =
    [
        ("left_thumb", DualShockState.L3),
        ("right_thumb", DualShockState.R3),
        ("left_shoulder", DualShockState.L1),
        ("right_shoulder", DualShockState.R1),
        ("a", DualShockState.Cross),
        ("b", DualShockState.Circle),
        ("x", DualShockState.Square),
        ("y", DualShockState.Triangle),
    ];
    private readonly CVRInput? _input;
    private readonly CVRSystem? _system;
    private readonly ulong[] _analog = [],
        _digital = [];
    private readonly VRActiveActionSet_t[] _sets = [];
    private readonly Thread? _poll;
    private readonly Action<ReadOnlySpan<byte>>? _report;
    private readonly VrPointerSession? _pointer;
    private readonly Func<VrPointerOsc.Touch, float, bool>? _pointerInput;
    private readonly Func<int>? _calibrationTarget;
    private readonly nuint[] _pads = new nuint[4];
    private readonly bool[] _attached = new bool[4];
    private readonly CemuhookServer? _primaryCemuhook, _secondaryCemuhook;
    private readonly bool _dualShock;
    private readonly int _dualShockMotionHand;
    private readonly VrControllerLayout _layout;
    private readonly object[] _padLocks = [new(), new(), new(), new()];
    private readonly object _deviceGate = new();
    private readonly long[] _updated = new long[4];
    private readonly Timer? _watchdog;
    private readonly TaskCompletionSource<Exception> _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _padCount;
    private readonly int _localSlot = -1;
    private nuint _server;
    private uint _bus;
    private bool _busCreated;
    private bool _vr;
    private ulong _haptic;
    private int _disposed;
    private int _remoteSlots;
    public Task<Exception> Fault => _fault.Task;
    public event Action<int>? ControllerTimedOut;
    public VrControllerBridge(bool capture = true, bool virtualPads = true, Action<ReadOnlySpan<byte>>? report = null, int padCount = 1, Action<string>? pointerStatus = null, bool pointerEnabled = true, Func<VrPointerOsc.Touch, float, bool>? pointerInput = null, Action<bool>? calibrationChanged = null, Action<PointerCalibrationProgress>? calibrationProgress = null, Func<int>? calibrationTarget = null, bool leftHanded = false, int port = 32341, bool dualShock = true, int dualShockMotionHand = 0, bool cemuhook = true, int cemuhookPort = CemuhookServer.DefaultPort)
    {
        try
        {
            _report = report;
            _dualShock = dualShock;
            _dualShockMotionHand = dualShockMotionHand is >= 0 and <= 2 ? dualShockMotionHand
                : throw new ArgumentOutOfRangeException(nameof(dualShockMotionHand));
            if (cemuhook && cemuhookPort is < 0 or > 65534) throw new ArgumentOutOfRangeException(nameof(cemuhookPort));
            _layout = new(leftHanded);
            _pointerInput = pointerInput;
            _calibrationTarget = calibrationTarget;
            _padCount = virtualPads
                ? padCount is >= 1 and <= 4
                    ? padCount
                    : throw new ArgumentOutOfRangeException(nameof(padCount))
                : 0;
            if (capture)
            {
                var initError = EVRInitError.None;
                var vr = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
                if (vr is null)
                    throw new InvalidOperationException(OpenVR.GetStringForHmdError(initError));
                _vr = true;
                _system = vr;
                _input = OpenVR.Input;
                Check(_input.SetActionManifestPath(Path.Combine(AppContext.BaseDirectory, "Input", "actions.json")));
                ulong set = 0;
                Check(_input.GetActionSetHandle("/actions/vrcoplay", ref set));
                ulong Handle(string name)
                {
                    ulong value = 0;
                    Check(_input.GetActionHandle($"/actions/vrcoplay/in/{name}", ref value));
                    return value;
                }
                _sets = [new() { ulActionSet = set }];
                _analog = AnalogActions.Select(Handle).ToArray();
                _digital = DigitalActions.Select(x => Handle(x.Name)).ToArray();
                _input.GetActionHandle(_layout.MainTipAction, ref _alignmentTipAction);
                _input.GetActionHandle(_layout.HapticAction, ref _haptic);
                _pointer = new(pointerStatus ?? (_ => { }), pointerEnabled, calibrationChanged, progressChanged: calibrationProgress, videoReady: false);
                _hologram = new();
            }
            if (virtualPads)
            {
                if (cemuhook)
                {
                    _primaryCemuhook = new(cemuhookPort, 0);
                    _secondaryCemuhook = new(cemuhookPort == 0 ? 0 : cemuhookPort + 1, 1);
                    _ = WatchCemuhookAsync(_primaryCemuhook);
                    _ = WatchCemuhookAsync(_secondaryCemuhook);
                }
                if (_dualShock)
                {
                    var config = new USBServerConfig { Address = $"127.0.0.1:{port}" };
                    Require(Native.NewUSBServer(ref config, out _server, 0), "start libVIIPER");
                    Require(Native.CreateUSBBus(_server, ref _bus), "create the controller bus");
                    _busCreated = true;
                }
                if (capture)
                    _localSlot = 0;
                if (_localSlot >= 0)
                    CreatePad(_localSlot);
                _watchdog = new(_ => NeutralizeStalePads(), null, 25, 25);
            }
            if (capture)
            {
                _poll = new(() =>
                {
                    try
                    {
                        Poll();
                    }
                    catch (Exception error)
                    {
                        if (_localSlot >= 0) lock (_padLocks[_localSlot])
                        {
                            NeutralizePad(_localSlot);
                        }
                        _fault.TrySetResult(error);
                    }
                })
                {
                    IsBackground = true,
                };
                _poll.Start();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    private void Poll()
    {
        var input = _input!;
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var motion = new VrMotionState();
        var secondaryMotion = new VrMotionState();
        Span<byte> report = stackalloc byte[ControllerReport.Size];
        while (Volatile.Read(ref _disposed) == 0)
        {
            if (SteamVrRuntime.QuitRequested(_system!))
                throw new InvalidOperationException("SteamVR closed.");
            _system!.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);
            var now = Stopwatch.GetTimestamp();
            var primary = PollMotion(motion, _layout.MainRole, now);
            var secondary = PollMotion(secondaryMotion, _layout.SecondaryRole, now);
            var state = DualShockState.Neutral;
            ushort buttons = 0;
            Check(input.UpdateActionState(_sets, SetSize));
            PollAlignment(input, poses);
            var left = Analog(0);
            var right = Analog(1);
            for (var i = 0; i < _digital.Length; i++)
            {
                var data = default(InputDigitalActionData_t);
                Check(
                    input.GetDigitalActionData(_digital[i], ref data, DigitalSize, OpenVR.k_ulInvalidInputValueHandle)
                );
                if (data.bActive && data.bState)
                    buttons |= DigitalActions[i].Button;
            }
            var leftTrigger = Analog(2).x;
            var rightTrigger = Analog(3).x;
            _layout.Apply(ref state, new(left.x, left.y), new(right.x, right.y), leftTrigger, rightTrigger, buttons);
            if (_layout.LeftHanded) rightTrigger = leftTrigger;
            var pointer = _pointer?.Current;
            var calibrating = pointer?.Trigger(rightTrigger, _calibrationTarget?.Invoke() ?? 0) == true;
            if (calibrating)
                state.ReleaseRightTrigger();
            if (_pointer is not null)
            {
                var pulse = 0;
                var touch = pointer?.Read(out pulse) ?? default;
                state.Touch1X = touch.X;
                state.Touch1Y = (ushort)Math.Round(touch.Y * 920d / 941);
                state.Touch1Active = touch.Active ? (byte)1 : (byte)0;
                if (_pointerInput?.Invoke(calibrating ? default : touch, rightTrigger) == true)
                    state.ReleaseRightTrigger();
                if (pulse != 0)
                    Haptic(pulse);
            }
            var frame = new ControllerFrame(state, primary, secondary, CemuhookServer.Timestamp, _layout.LeftHanded);
            if (_localSlot >= 0)
                lock (_padLocks[_localSlot])
                {
                    UpdatePad(_localSlot, frame);
                }
            ControllerReport.Write(report, frame);
            _report?.Invoke(report);
            Thread.Sleep(8);
        }
        ControllerMotion PollMotion(VrMotionState source, ETrackedControllerRole role, long now)
        {
            var index = _system!.GetTrackedDeviceIndexForControllerRole(role);
            var pose = index < poses.Length ? poses[index] : default;
            return source.ReadMotion(index, pose, now);
        }
    }
    private InputAnalogActionData_t Analog(int index)
    {
        var data = default(InputAnalogActionData_t);
        Check(_input!.GetAnalogActionData(_analog[index], ref data, AnalogSize, OpenVR.k_ulInvalidInputValueHandle));
        return data.bActive ? data : default;
    }
    public void SetRemote(int slot, ReadOnlySpan<byte> report)
    {
        if ((uint)slot >= _padCount || !ControllerReport.TryRead(report, out var frame) ||
            (Volatile.Read(ref _remoteSlots) & (1 << slot)) == 0 || !Monitor.TryEnter(_padLocks[slot]))
            return;
        try
        {
            if (_disposed != 0 || !_attached[slot] || (Volatile.Read(ref _remoteSlots) & (1 << slot)) == 0)
                return;
            UpdatePad(slot, frame);
            _updated[slot] = Stopwatch.GetTimestamp();
        }
        catch (Exception error)
        {
            _fault.TrySetResult(error);
        }
        finally { Monitor.Exit(_padLocks[slot]); }
    }
    public void SetRemoteSlots(int slots)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        slots &= ((1 << _padCount) - 1) & ~(_localSlot < 0 ? 0 : 1 << _localSlot);
        var changed = Interlocked.Exchange(ref _remoteSlots, slots) ^ slots;
        if (changed == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (_deviceGate)
                    for (var slot = 0; slot < _padCount; slot++)
                    {
                        if (slot == _localSlot) continue;
                        lock (_padLocks[slot])
                        {
                            if (_disposed != 0) return;
                            if ((Volatile.Read(ref _remoteSlots) & (1 << slot)) != 0 && !_attached[slot]) CreatePad(slot);
                            if ((Volatile.Read(ref _remoteSlots) & (1 << slot)) == 0 && _attached[slot]) RemovePad(slot);
                        }
                    }
            }
            catch (Exception error) { _fault.TrySetResult(error); }
        });
    }
    private void CreatePad(int slot)
    {
        var meta = new DS4MetaState { SerialNumber = $"5652434F504C{slot + 1:X4}" };
        try
        {
            if (_dualShock)
            {
                Require(Native.CreateDS4Device(_server, out _pads[slot], _bus, 1, 0, 0, ref meta), "create a DualShock controller");
                Require(Native.SetDS4DeviceState(_pads[slot], DualShockState.Neutral), "initialize a DualShock controller");
            }
            _attached[slot] = true;
            _primaryCemuhook?.SetConnected(slot, true);
            _secondaryCemuhook?.SetConnected(slot, true);
        }
        catch { RemovePad(slot); throw; }
    }
    private void RemovePad(int slot)
    {
        _primaryCemuhook?.SetConnected(slot, false);
        _secondaryCemuhook?.SetConnected(slot, false);
        if (_pads[slot] != 0) Native.RemoveDS4Device(_pads[slot]);
        _pads[slot] = 0;
        _attached[slot] = false;
        _updated[slot] = 0;
    }
    internal (int Primary, int Secondary)? CemuhookPorts => _primaryCemuhook is not null && _secondaryCemuhook is not null
        ? (_primaryCemuhook.Port, _secondaryCemuhook.Port) : null;
    private void UpdatePad(int slot, in ControllerFrame frame)
    {
        if (_pads[slot] != 0)
            Require(Native.SetDS4DeviceState(_pads[slot], frame.ToDualShock(_dualShockMotionHand)), "update the DualShock controller");
        _primaryCemuhook?.Update(slot, frame.Controls, frame.Primary, frame.Timestamp);
        _secondaryCemuhook?.Update(slot, frame.Controls, frame.Secondary, frame.Timestamp);
    }
    private bool NeutralizePad(int slot)
    {
        try { UpdatePad(slot, ControllerFrame.Neutral); return true; }
        catch (Exception error) { _fault.TrySetResult(error); return false; }
    }
    private async Task WatchCemuhookAsync(CemuhookServer server)
    {
        try { _fault.TrySetResult(await server.Fault.ConfigureAwait(false)); }
        catch (OperationCanceledException) { }
    }
    private void NeutralizeStalePads()
    {
        for (var slot = 0; slot < _padCount; slot++)
            if (Monitor.TryEnter(_padLocks[slot])) try
            {
                var updated = _updated[slot];
                if (slot != _localSlot && updated != 0 && _attached[slot] && Stopwatch.GetElapsedTime(updated).TotalMilliseconds >= 250)
                {
                    if (NeutralizePad(slot))
                    {
                        _updated[slot] = 0;
                        ControllerTimedOut?.Invoke(slot);
                    }
                }
            }
            finally { Monitor.Exit(_padLocks[slot]); }
    }
    private static void Check(EVRInputError error)
    {
        if (error != EVRInputError.None)
            throw new InvalidOperationException($"OpenVR input error: {error}.");
    }
    private static void Require(bool success, string action)
    {
        if (!success)
            throw new InvalidOperationException($"Could not {action}.");
    }
    private void Haptic(int pulse)
    {
        var action = _haptic;
        if (action == 0) return;
        var duration = pulse == 3 ? .18f : .035f;
        _input!.TriggerHapticVibrationAction(action, 0, duration, 120, .65f, OpenVR.k_ulInvalidInputValueHandle);
        if (pulse is 2 or 5)
            _input.TriggerHapticVibrationAction(action, .09f, duration, 120, .65f, OpenVR.k_ulInvalidInputValueHandle);
    }
    internal bool CanRetryPointer => Volatile.Read(ref _disposed) == 0 && _pointer is not null;
    internal string? PointerStatus => _pointer?.Status;
    internal Task RetryPointerAsync() => _pointer?.RetryAsync() ?? Task.CompletedTask;
    internal void RecalibratePointer() => _pointer?.Current?.Recalibrate();
    internal void EnablePointer(bool enabled) => _pointer?.SetEnabled(enabled);
    internal Task VideoStartedAsync() => _pointer?.VideoStartedAsync() ?? Task.CompletedTask;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _fault.TrySetCanceled();
        _poll?.Join();
        _hologram?.Dispose();
        if (_vr)
        {
            OpenVR.Shutdown();
            _vr = false;
        }
        _watchdog?.Dispose();
        _pointer?.Dispose();
        lock (_deviceGate)
        {
            for (var slot = 0; slot < _padCount; slot++)
                lock (_padLocks[slot]) RemovePad(slot);
            if (_server != 0)
            {
                try
                {
                    if (_busCreated) Native.RemoveUSBBus(_server, _bus);
                    _busCreated = false;
                    _bus = 0;
                }
                finally
                {
                    Native.CloseUSBServer(_server);
                    _server = 0;
                }
            }
        }
        _primaryCemuhook?.Dispose();
        _secondaryCemuhook?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct USBServerConfig
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string Address;
        public ulong ConnectionTimeout,
            DeviceHandlerConnectTimeout;
        public uint WriteBatchFlushInterval;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DS4MetaState
    {
        [MarshalAs(UnmanagedType.LPStr)] public string SerialNumber;
        [MarshalAs(UnmanagedType.LPStr)] public string? Board;
        public byte BatteryStatus;
        public double TemperatureCelsius, BatteryVoltage;
    }
    private static class Native
    {
        private const string Library = "libVIIPER";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool NewUSBServer(ref USBServerConfig config, out nuint handle, nint logCallback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CloseUSBServer(nuint handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CreateUSBBus(nuint handle, ref uint bus);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool RemoveUSBBus(nuint handle, uint bus);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CreateDS4Device(nuint server, out nuint device, uint bus, byte autoAttach, ushort vendor, ushort product, ref DS4MetaState meta);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SetDS4DeviceState(nuint device, DualShockState state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool RemoveDS4Device(nuint device);
    }
}
