// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Valve.VR;
namespace VRCoplay;
internal sealed record HologramRequest(string Model, ulong DevicePath,
    PointerPose Target, bool Matched, float Hold, int Captures, bool Tracking = true,
    uint DeviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid);
internal sealed class VrControllerHologram : IDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _worker;
    private HologramRequest? _request;
    private ControllerHologramScene? _scene;
    private string? _error;
    internal ControllerHologramScene? Scene => Volatile.Read(ref _scene);
    internal string? Error => Volatile.Read(ref _error);
    internal void Update(HologramRequest? request) => Volatile.Write(ref _request, request);
    internal void TrackingLost()
    {
        if (Volatile.Read(ref _request) is { } previous)
            Update(previous with { Tracking=false,Matched=false,Hold=0 });
    }
    internal VrControllerHologram()
    {
        _worker = new(Run) { IsBackground=true,Name="Controller hologram" };
        _worker.Start();
    }
    private void Run()
    {
        SteamVrCalibrationOverlay? overlay = null;
        SteamVrControllerModel? loader = null; string modelName=""; ulong devicePath=0;
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        long retryAt=0,statsAt=Stopwatch.GetTimestamp(); int failures=0,frames=0,trackingGaps=0; double renderMs=0;
        bool active=false;
        try
        {
            var models = OpenVR.RenderModels; var api = OpenVR.Overlay; var system = OpenVR.System;
            while (!_stop.IsSet)
            {
                var request = Volatile.Read(ref _request);
                if (request is null)
                {
                    Volatile.Write(ref _scene,null); overlay?.Hide();
                    if (active) { Log("Hidden: calibration ended or pointer disabled."); active=false; }
                    _stop.Wait(20); continue;
                }
                if (request.Model != modelName || request.DevicePath != devicePath)
                {
                    (modelName,devicePath)=(request.Model,request.DevicePath);
                    loader=new(modelName,devicePath); Volatile.Write(ref _error,null);
                    Volatile.Write(ref _scene,null); overlay?.Hide(); active=false;
                    Log("Loading controller model: " + modelName);
                }
                loader!.Poll(models);
                if (loader.Error is { } modelError)
                { Volatile.Write(ref _error,modelError); _stop.Wait(50); continue; }
                if (loader.Mesh is not { } mesh) { _stop.Wait(10); continue; }
                var scene = new ControllerHologramScene(mesh,default,
                    request.Target,request.Matched,request.Hold,request.Captures,request.Tracking);
                if (Stopwatch.GetTimestamp()<retryAt) { _stop.Wait(20); continue; }
                try
                {
                    overlay ??= new(system,api);
                    api.WaitFrameSync(20);
                    if (_stop.IsSet) break;
                    if (Volatile.Read(ref _request) is null) continue;
                    system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding,Prediction(system),poses);
                    var head = poses[0].mDeviceToAbsoluteTracking;
                    if (!poses[0].bPoseIsValid || !poses[0].bDeviceIsConnected)
                    { trackingGaps++; _stop.Wait(5); continue; }
                    scene = TrackedScene(scene,request.DeviceIndex,poses);
                    long began=Stopwatch.GetTimestamp();
                    var current=scene.Tracking?poses[request.DeviceIndex].mDeviceToAbsoluteTracking:default;
                    overlay.Render(scene,head,current,system);
                    overlay.Show(); failures=0; Volatile.Write(ref _error,null);
                    Volatile.Write(ref _scene,scene);
                    if (!active) { Log("Visible: room target and live controller in stereo, at physical scale."); active=true; }
                    renderMs+=Stopwatch.GetElapsedTime(began).TotalMilliseconds; frames++;
                }
                catch (Exception error)
                {
                    if (++failures==1) Log(error.Message);
                    retryAt=Stopwatch.GetTimestamp()+Stopwatch.Frequency/10;
                    if (failures>=3)
                    {
                        overlay?.Dispose(); overlay=null; active=false;
                        Volatile.Write(ref _error,error.Message);
                        retryAt=Stopwatch.GetTimestamp()+Stopwatch.Frequency;
                    }
                }
                if (Stopwatch.GetElapsedTime(statsAt).TotalSeconds>=5)
                {
                    Log($"GPU stereo frames={frames}; submit={(frames==0?0:renderMs/frames):F2} ms; headset gaps={trackingGaps}; hand tracked={scene.Tracking}; captures={request.Captures}.");
                    statsAt=Stopwatch.GetTimestamp(); frames=0; trackingGaps=0; renderMs=0;
                }
            }
        }
        catch (Exception error) { Log(error.Message); Volatile.Write(ref _error,error.Message); }
        finally
        {
            Volatile.Write(ref _scene,null);
            try { overlay?.Dispose(); } catch (Exception error) { Log(error.Message); }
        }
    }
    internal static ControllerHologramScene TrackedScene(ControllerHologramScene scene,uint index,TrackedDevicePose_t[] poses)
    {
        if (index>=poses.Length || !poses[index].bPoseIsValid || !poses[index].bDeviceIsConnected)
            return scene with { Tracking=false };
        var current=SteamVrControllerModel.Pose(poses[index].mDeviceToAbsoluteTracking);
        return scene with { Current=current,Tracking=current.Valid };
    }
    private static float Prediction(CVRSystem system)
    {
        var error=ETrackedPropertyError.TrackedProp_Success;
        float frequency=system.GetFloatTrackedDeviceProperty(0,ETrackedDeviceProperty.Prop_DisplayFrequency_Float,ref error);
        if (!float.IsFinite(frequency) || frequency<30) frequency=90;
        float photons=system.GetFloatTrackedDeviceProperty(0,ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float,ref error);
        if (!float.IsFinite(photons) || photons<0) photons=0;
        float since=0; ulong frame=0; system.GetTimeSinceLastVsync(ref since,ref frame);
        if (!float.IsFinite(since) || since<0) since=0;
        return Math.Clamp(1/frequency+photons-since,0,.05f);
    }
    private static void Log(string message)
    {
        Debug.WriteLine("Controller hologram: " + message);
        try
        {
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VRCoplay");
            Directory.CreateDirectory(folder); string path=Path.Combine(folder,"controller-hologram.log");
            if (File.Exists(path) && new FileInfo(path).Length>131072) File.WriteAllText(path,"");
            File.AppendAllText(path,$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
    public void Dispose() { _stop.Set(); _worker.Join(); _stop.Dispose(); }
}
