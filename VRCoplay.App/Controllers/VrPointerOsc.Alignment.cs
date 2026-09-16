// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using FastOSC;
using VRC.OSCQuery;
namespace VRCoplay;
internal readonly record struct PointerAlignmentStatus(bool Available, int Phase, int Poses, string Message, float X, float Y,
    int Hint = 0, float Progress = 0, int GuideVersion = 0);
internal sealed partial class VrPointerOsc
{
    private const string AlignPrefix = "/avatar/parameters/VRCoplay/Alignment";
    private static readonly string[] AlignmentOffsets = ["PositionX", "PositionY", "PositionZ", "RotationX", "RotationY", "RotationZ", "X", "Y"];
    private static readonly string[] AlignmentMarkers = ["OX", "OY", "OZ", "RX", "RY", "RZ", "UX", "UY", "UZ"];
    private readonly float[] _alignmentValues = Enumerable.Repeat(.5f, 8).ToArray();
    private readonly float[] _alignmentMarkers = new float[9];
    private readonly List<PointerAlignmentSample> _alignmentPoses = [];
    private readonly PointerAlignmentCapture _alignmentCapture = new();
    private readonly List<PointerAlignmentMeasurement> _alignmentMeasurements = [];
    private int _alignmentPhase, _alignmentAxes;
    private bool _hasAlignment, _alignmentDone, _alignmentFirstUse;
    private long _alignmentStarted, _alignmentLastTick;
    private long _alignmentCompletedAt;
    private long _alignmentTriggerAt;
    private bool _alignmentTriggerReleased;
    private int _alignmentHint, _alignmentGuideVersion;
    private string _alignmentMessage = "", _alignmentDevice = "";
    private string _alignmentReference = "action pose";
    private string? _alignmentProfileKey;
    private readonly string _alignmentProfilePath;
    private readonly Dictionary<string, float[]> _alignmentProfiles;
    private readonly Dictionary<string, PointerAlignmentReference> _alignmentReferences;
    private readonly string _alignmentReferencePath;
    internal PointerAlignmentStatus Alignment { get { lock (_gate) return new(_hasAlignment && _asset && _enabled, _alignmentPhase,
        _alignmentPoses.Count, _alignmentMessage, (_alignmentValues[6] - .5f) * 90, (_alignmentValues[7] - .5f) * 90,
        _alignmentPhase == 0 ? (_alignmentCompletedAt != 0 ? 10 : 0) : _alignmentHint,
        _alignmentCapture.Progress,_alignmentGuideVersion); } }
    private void AdvanceAlignmentCompletionLocked()
    {
        if (_alignmentCompletedAt == 0 || Stopwatch.GetElapsedTime(_alignmentCompletedAt).TotalSeconds < 1.6) return;
        _alignmentCompletedAt = 0;
        _trigger = true;
        SetStatusLocked();
    }
    private void AdvertiseAlignment()
    {
        foreach (var name in AlignmentOffsets.Concat(AlignmentMarkers)) _query.AddEndpoint<float>(AlignPrefix + name, Attributes.AccessValues.WriteOnly);
        foreach (var name in new[] { "Auto", "Skip", "Reset", "Done" }) _query.AddEndpoint<bool>(AlignPrefix + name, Attributes.AccessValues.WriteOnly);
    }
    private void DiscoverAlignmentLocked(OSCQueryRootNode tree)
    {
        _hasAlignment = AlignmentMarkers.All(p => Supports(tree, AlignPrefix + p, Attributes.AccessValues.ReadOnly)) &&
            AlignmentOffsets.All(p => Supports(tree, AlignPrefix + p, Attributes.AccessValues.ReadWrite)) &&
            Supports(tree, AlignPrefix + "State", Attributes.AccessValues.WriteOnly) && Supports(tree, AlignPrefix + "Done", Attributes.AccessValues.ReadWrite);
        if (!_hasAlignment) return;
        for (int i = 0; i < 8; i++)
        {
            var value = tree.GetNodeWithPath(AlignPrefix + AlignmentOffsets[i])?.Value?.FirstOrDefault();
            _alignmentValues[i] = value is not null && float.TryParse(value.ToString(), out var number) && float.IsFinite(number) ? Math.Clamp(number, 0, 1) : .5f;
        }
        _alignmentDone = tree.GetNodeWithPath(AlignPrefix + "Done")?.Value?.FirstOrDefault() is { } done && Convert.ToBoolean(done);
        _alignmentProfileKey = null;
        if (!_alignmentDone && _enabled) SetAlignmentStateLocked(1, "Match your controller to the blue controller.");
        else SetAlignmentStateLocked(0, "Your controller alignment is saved.");
    }
    private bool ReceiveAlignmentLocked(OSCMessage message)
    {
        if (!_hasAlignment || !message.Address.StartsWith(AlignPrefix, StringComparison.Ordinal)) return false;
        var name = message.Address[AlignPrefix.Length..]; var value = message.Arguments.FirstOrDefault();
        int index = Array.IndexOf(AlignmentMarkers, name);
        if (index >= 0 && value is float coordinate)
        {
            _alignmentMarkers[index] = coordinate;
            _alignmentAxes |= 1 << index;
        }
        else if ((index = Array.IndexOf(AlignmentOffsets, name)) >= 0 && value is float number && float.IsFinite(number))
        {
            number = Math.Clamp(number, 0, 1);
            if (Math.Abs(number - _alignmentValues[index]) > .00001f)
            {
                _alignmentValues[index] = number;
                SendLocked(Lock, false); ResetLocked();
                if (_alignmentPhase != 0) SkipAlignment();
                SaveAlignmentProfileLocked(); _lastStatus = null; SetStatusLocked();
            }
        }
        else if (name == "Done" && value is bool done)
        {
            _alignmentDone = done;
            if (done && (_alignmentPhase == 1 || _alignmentFirstUse))
            {
                CancelAlignmentLocked();
                SetAlignmentStateLocked(0, "Your controller alignment is saved.");
            }
        }
        else if (value is true)
        {
            if (name == "Auto") StartAlignment();
            else if (name == "Skip") SkipAlignment();
            else if (name == "Reset") ResetAlignment();
        }
        return true;
    }
    internal void StartAlignment(bool automatic = false)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_enabled || !_asset || !_hasAlignment || automatic && (_alignmentDone || _alignmentPhase != 1)) return;
            _alignmentFirstUse = automatic;
            _alignmentCompletedAt = 0;
            if (!automatic) _alignmentGuideVersion++;
            _alignmentPoses.Clear(); _alignmentMeasurements.Clear(); ClearAlignmentStillLocked(); _alignmentAxes = 0; _alignmentLastTick = 0; _pulse = 0;
            _alignmentStarted = Stopwatch.GetTimestamp();
            SendLocked(Lock, false); ResetLocked();
            SetAlignmentStateLocked(2, "Match your controller to the blue controller.");
        }
    }
    internal void SkipAlignment()
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_asset || !_hasAlignment) return;
            CancelAlignmentLocked(); _alignmentDone = true;
            SendLocked(AlignPrefix + "Done", true); SaveAlignmentProfileLocked();
            SendLocked(Lock, false); ResetLocked();
            _alignmentMessage = "Alignment skipped. Use the sliders to adjust your aim.";
            _lastStatus = null; SetStatusLocked();
        }
    }
    internal void SetAlignmentTrim(float xDegrees, float yDegrees)
    {
        if (!float.IsFinite(xDegrees) || !float.IsFinite(yDegrees)) return;
        lock (_gate)
        {
            if (_disposed != 0 || !_asset || !_hasAlignment) return;
            if (_alignmentPhase != 0 || !_alignmentDone) SkipAlignment();
            _alignmentValues[6] = Math.Clamp(xDegrees / 90 + .5f, 0, 1);
            _alignmentValues[7] = Math.Clamp(yDegrees / 90 + .5f, 0, 1);
            SendAlignmentValueLocked("X", _alignmentValues[6]); SendAlignmentValueLocked("Y", _alignmentValues[7]);
            SendLocked(Lock, false); ResetLocked(); SaveAlignmentProfileLocked();
        }
    }
    internal void ResetAlignment()
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_asset || !_hasAlignment) return;
            CancelAlignmentLocked(); Array.Fill(_alignmentValues, .5f); ApplyAlignmentValuesLocked();
            _alignmentGuideVersion++;
            _alignmentDone = false; SendLocked(AlignPrefix + "Done", false);
            if (_avatar is not null)
            {
                foreach (var key in _alignmentProfiles.Keys.Where(k => k.StartsWith(_avatar + "|", StringComparison.Ordinal)).ToArray())
                    _alignmentProfiles.Remove(key);
                foreach (var key in _alignmentReferences.Keys.Where(k => k.StartsWith(_avatar + "|", StringComparison.Ordinal)).ToArray())
                    _alignmentReferences.Remove(key);
            }
            PersistAlignmentReferences();
            PersistAlignmentProfiles(); SendLocked(Lock, false); ResetLocked();
            SetAlignmentStateLocked(1, "Match your controller to the blue controller.");
        }
    }
    internal void TrackAlignment(PointerPose? tip, string device, bool targetMatched = true)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_asset || !_enabled || !_hasAlignment) return;
            if (device.Length > 0 && device != _alignmentDevice)
            {
                bool changed = _alignmentDevice.Length > 0;
                _alignmentDevice = device; _alignmentProfileKey = null;
                if (changed)
                {
                    CancelAlignmentLocked(); SendLocked(Lock, false); ResetLocked();
                    SetAlignmentStateLocked(_alignmentDone ? 0 : 1, _alignmentDone
                        ? "Your controller alignment is saved. Choose Align controller to adjust it again."
                        : "Match your controller to the blue controller.");
                }
            }
            if (_alignmentProfileKey is null && _avatar is not null && _alignmentDevice.Length > 0)
            {
                _alignmentProfileKey = _avatar + "|" + _alignmentDevice;
                if (_alignmentDone) SaveAlignmentProfileLocked();
            }
            if (_alignmentPhase != 2) return;
            var now = Stopwatch.GetTimestamp();
            if (!targetMatched) { ClearAlignmentStillLocked(); return; }
            if (tip is not { Valid: true } controller || _alignmentAxes != 511 || _sampleAt == 0 || Stopwatch.GetElapsedTime(_sampleAt, now).TotalMilliseconds > 150)
            {
                ClearAlignmentStillLocked();
                if (Stopwatch.GetElapsedTime(_alignmentStarted, now).TotalSeconds > 3)
                {
                    _alignmentHint = tip is not { Valid: true } ? 6 : 7;
                    AlignmentMessageLocked(_alignmentHint == 6 ? "Controller tracking unavailable. Check your controller’s connection." : "Hold your controller in front of you.");
                }
                return;
            }
            if (_sampleAt == _alignmentLastTick) return;
            _alignmentLastTick = _sampleAt;
            if (_alignmentMarkers.Any(v => !float.IsFinite(v) || v is <= .001f or >= .999f))
            { ClearAlignmentStillLocked(); _alignmentHint = 7; AlignmentMessageLocked("Hold your controller in front of you."); return; }
            Vector3 Marker(int i) => new((_alignmentMarkers[i] - .5f) * 6, (_alignmentMarkers[i + 1] - .5f) * 6, (_alignmentMarkers[i + 2] - .5f) * 6);
            if (!PointerPose.TryContactMarkers(Marker(0), Marker(3), Marker(6), out var hand)) { ClearAlignmentStillLocked(); return; }
            var sample = new PointerAlignmentSample(hand, controller);
            if (!_alignmentCapture.Add(sample, now / (double)Stopwatch.Frequency, out var measurement)) return;
            var measured = measurement.Sample;
            if (_alignmentPoses.Any(p => Vector3.Distance(p.Tip.Position, measured.Tip.Position) < .04f && PointerPose.Angle(p.Tip.Rotation, measured.Tip.Rotation) < .20f))
            { ClearAlignmentStillLocked(); GuideAlignmentLocked(); return; }
            _alignmentPoses.Add(measured); _alignmentMeasurements.Add(measurement); ClearAlignmentStillLocked(); _pulse = 4;
            SendAlignmentIntLocked("Count", _alignmentPoses.Count);
            if (_alignmentPoses.Count >= 4)
            {
                bool solved = PointerAlignmentSolver.TrySolve(_alignmentPoses, out var fit, out var issue);
                if (solved && _alignmentMeasurements.Any(m => m.HandPositionNoise / fit.Scale > .0015f))
                { solved = false; issue = "Avatar hand tracking did not settle within 1.5 mm."; }
                if (solved)
                {
                    var correction = _alignmentProfileKey is { } key && _alignmentReferences.TryGetValue(key, out var previous) && previous.Valid
                        ? previous.Correction : new PointerPose(Vector3.Zero, Quaternion.Identity);
                    var reference = new PointerAlignmentReference(fit, correction);
                    var applied = reference.Apply(fit);
                    if (Math.Max(Math.Abs(applied.Position.X), Math.Max(Math.Abs(applied.Position.Y), Math.Abs(applied.Position.Z))) > .5f)
                    {
                        SaveAlignmentDiagnosticLocked(fit, "The saved fine-tuning exceeds the avatar's adjustment range.");
                        SetAlignmentStateLocked(5, "Couldn’t align your controller. Press the trigger to try again."); return;
                    }
                    SaveAlignmentDiagnosticLocked(fit, "accepted");
                    var position = applied.Position; var rotation = PointerAlignmentSolver.Euler(applied.Rotation);
                    _alignmentValues[0] = position.X + .5f; _alignmentValues[1] = position.Y + .5f; _alignmentValues[2] = position.Z + .5f;
                    _alignmentValues[3] = rotation.X / 360 + .5f; _alignmentValues[4] = rotation.Y / 360 + .5f; _alignmentValues[5] = rotation.Z / 360 + .5f;
                    if (_alignmentProfileKey is { } profileKey) _alignmentReferences[profileKey] = reference;
                    ApplyAlignmentValuesLocked();
                    _alignmentDone = true; SendLocked(AlignPrefix + "Done", true); SaveAlignmentProfileLocked();
                    _alignmentCompletedAt = now;
                    SetAlignmentStateLocked(0, "Your controller alignment is saved.");
                    SendLocked(Lock, false); ResetLocked(); _pulse = 5; return;
                }
                Debug.WriteLine("Controller alignment: " + issue);
                SaveAlignmentDiagnosticLocked(fit.Scale > 0 ? fit : null, issue);
                SetAlignmentStateLocked(5, "Couldn’t align your controller. Press the trigger to try again.");
                return;
            }
            GuideAlignmentLocked();
        }
    }
    private void GuideAlignmentLocked()
    {
        _alignmentHint = 2;
        AlignmentMessageLocked("Match your controller to the blue controller.");
    }
    internal void HologramGuidance(bool ready, bool matched, bool failed, bool tracking = true, bool placement = true)
    {
        lock (_gate)
        {
            if (_alignmentPhase is not (1 or 2)) return;
            _alignmentHint = !tracking ? 6 : !placement ? 7 : failed ? 9 : !ready ? 5 : matched ? 3 : 2;
            AlignmentMessageLocked(_alignmentHint switch {
                3 => "Hold still.", 5 => "Loading your controller…", 6 => "Controller tracking unavailable.",
                7 => "Hold your controller in front of you.",
                9 => "Controller display unavailable. Use the adjustment sliders to continue.",
                _ => "Match your controller to the blue controller."
            });
        }
    }
    private void AlignmentTriggerLocked(float value)
    {
        if (!float.IsFinite(value)) return;
        if (value < .5f)
        {
            bool tap = _alignmentTriggerAt != 0;
            _alignmentTriggerAt = 0; _alignmentTriggerReleased = true;
            if (tap && _alignmentPhase is 1 or 5) StartAlignment();
        }
        else if (value >= .75f && _alignmentTriggerReleased)
        {
            if (_alignmentTriggerAt == 0) _alignmentTriggerAt = Stopwatch.GetTimestamp();
            else if (Stopwatch.GetElapsedTime(_alignmentTriggerAt).TotalSeconds >= 1.2)
            { _alignmentTriggerAt = 0; _alignmentTriggerReleased = false; SkipAlignment(); }
        }
    }
    private void ClearAlignmentStillLocked() => _alignmentCapture.Clear();
    private void CancelAlignmentLocked()
    {
        _alignmentPoses.Clear(); _alignmentMeasurements.Clear(); ClearAlignmentStillLocked(); _alignmentPhase = 0; _alignmentMessage = ""; _alignmentCompletedAt = 0;
        _alignmentTriggerAt = 0; _alignmentTriggerReleased = false; _alignmentHint = 0; _alignmentFirstUse = false;
        if (_pulse >= 4) _pulse = 0;
        if (_hasAlignment && _asset) { SendAlignmentIntLocked("State", 0); SendAlignmentIntLocked("Count", 0); }
    }
    private void SetAlignmentStateLocked(int phase, string message)
    {
        _alignmentPhase = phase; _alignmentMessage = message;
        if (phase == 0) _alignmentFirstUse = false;
        _alignmentHint = phase switch { 1 => 1, 2 => 2, 5 => 8, _ => 0 };
        if (_hasAlignment && _asset) { SendAlignmentIntLocked("State", phase); SendAlignmentIntLocked("Count", _alignmentPoses.Count); }
        _lastStatus = null; SetStatusLocked();
    }
    private void AlignmentMessageLocked(string message) { if (_alignmentMessage == message) return; _alignmentMessage = message; _lastStatus = null; SetStatusLocked(); }
    private void ApplyAlignmentValuesLocked() { for (int i = 0; i < 8; i++) SendAlignmentValueLocked(AlignmentOffsets[i], _alignmentValues[i]); }
    private void SendAlignmentValueLocked(string name, float value) { try { _sender?.Send(OSCEncoder.Encode(new OSCMessage(AlignPrefix + name, value))); } catch (Exception error) { Debug.WriteLine(error.Message); } }
    private void SendAlignmentIntLocked(string name, int value) { try { _sender?.Send(OSCEncoder.Encode(new OSCMessage(AlignPrefix + name, value))); } catch (Exception error) { Debug.WriteLine(error.Message); } }
    private static Dictionary<string, float[]> LoadAlignmentProfiles(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }
    private void SaveAlignmentProfileLocked()
    {
        if (!_alignmentDone || _alignmentProfileKey is null) return;
        if (_alignmentReferences.TryGetValue(_alignmentProfileKey, out var reference) && reference.Valid)
        {
            var observed = reference.Observe(_alignmentValues);
            if (observed.Valid) { _alignmentReferences[_alignmentProfileKey] = observed; PersistAlignmentReferences(); }
        }
        _alignmentProfiles[_alignmentProfileKey] = (float[])_alignmentValues.Clone(); PersistAlignmentProfiles();
    }
    private static Dictionary<string, PointerAlignmentReference> LoadAlignmentReferences(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, PointerAlignmentReference>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true }) ?? []; }
        catch { return []; }
    }
    private void PersistAlignmentReferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_alignmentReferencePath)!);
            File.WriteAllText(_alignmentReferencePath + ".tmp", JsonSerializer.Serialize(_alignmentReferences, new JsonSerializerOptions { IncludeFields = true }));
            File.Move(_alignmentReferencePath + ".tmp", _alignmentReferencePath, true);
        }
        catch (Exception error) { Debug.WriteLine("Controller reference save: " + error.Message); }
    }
    private void PersistAlignmentProfiles()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_alignmentProfilePath)!); File.WriteAllText(_alignmentProfilePath + ".tmp", JsonSerializer.Serialize(_alignmentProfiles)); File.Move(_alignmentProfilePath + ".tmp", _alignmentProfilePath, true); }
        catch (Exception error) { Debug.WriteLine("Controller alignment save: " + error.Message); }
    }
    internal void AlignmentReference(string reference) { lock (_gate) _alignmentReference = reference; }
    private void SaveAlignmentDiagnosticLocked(PointerAlignmentFit? fit, string result)
    {
        try
        {
            string path = Path.ChangeExtension(_alignmentProfilePath, ".last-fit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new {
                Timestamp = DateTimeOffset.UtcNow, Result = result, Fit = fit, Measurements = _alignmentMeasurements,
                PreviousParameters = _alignmentValues, Reference = _alignmentReference, Algorithm = 2,
                ReferenceCorrection = _alignmentProfileKey is { } key && _alignmentReferences.TryGetValue(key, out var reference) ? reference.Correction : (PointerPose?)null,
                Note = "Fit statistics describe SDK pose registration. ReferenceCorrection is user fine-tuning; neither is a guarantee of absolute visual alignment."
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) { Debug.WriteLine("Controller alignment diagnostics: " + error.Message); }
    }
}
