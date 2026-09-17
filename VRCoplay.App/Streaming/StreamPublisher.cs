// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Gst;
using Task = System.Threading.Tasks.Task;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal sealed class StreamPublisher : IDisposable
{
    private const string VideoQueue = "queue max-size-buffers=1 max-size-bytes=0 max-size-time=0 leaky=downstream";
    private readonly Pipeline _pipeline;
    private readonly Element _mixer;
    private readonly Gst.App.AppSrc? _audio;
    private readonly int _width, _height, _fps;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<StreamOverlayTarget> _overlays = [];
    private Bin? _capture;
    private TaskCompletionSource? _captureFailed;
    private long _source, _captured, _frames;
    private nint _window;
    private bool? _fallback;
    private bool _disposed;
    internal event Action<bool>? FallbackChanged;
    internal event Action<Exception>? OverlayFailed;
    internal event Action<long>? Frame;
    internal event Action<string>? Log;
    internal StreamPublisher(string tools, int width, int height, int fps, StreamSettings settings,
        VideoEncoder encoder, string output, StreamOverlay overlays,
        Func<(int Hint, float Progress)>? alignment = null, Func<ControllerHologramScene?>? hologram = null,
        string? localOutput = null)
    {
        (_width, _height, _fps) = (width, height, fps);
        string Sink(string name, string url) => $"rtspclientsink name={name} location=\"{url}\" protocols=tcp latency=0 rtx-time=0";
        string Branch(string name) => $"video. ! {VideoQueue} ! " +
            (localOutput is null ? "" : "d3d11convert ! video/x-raw(memory:D3D11Memory),format=RGBA ! ") +
            $"d3d11overlay name=overlay_{name} ! d3d11convert ! " +
            $"video/x-raw(memory:D3D11Memory),format=NV12 ! {encoder.Pipeline(fps, settings.Quality, settings.EncoderProperties)} ! h264parse name=parser_{name} ! {name}.";
        var graph = $"d3d11testsrc is-live=true pattern=black ! {CanvasCaps()} ! mix.sink_0 " +
            $"d3d11compositor name=mix force-live=true background=black ! {CanvasCaps()} ! tee name=video " +
            Sink("publish", output) + " " + Branch("publish");
        if (localOutput is not null) graph += " " + Sink("local", localOutput) + " " + Branch("local");
        if (settings.Audio)
        {
            var audioQueue = $"queue max-size-buffers=0 max-size-bytes=0 max-size-time={2_000_000_000L / fps + 60_000_000} leaky=downstream";
            graph += " appsrc name=audio is-live=true format=time block=false min-latency=20000000 max-latency=60000000 max-bytes=0 max-buffers=0 max-time=60000000 leaky-type=downstream " +
                "caps=\"audio/x-raw,format=F32LE,rate=44100,channels=2,layout=interleaved\" ! " +
                "audiomixer force-live=true ignore-inactive-pads=true latency=0 output-buffer-duration=10000000 ! " +
                "audioconvert ! audio/x-raw,format=S16BE,rate=44100,channels=2,layout=interleaved ! tee name=sound " +
                $"sound. ! {audioQueue} ! publish.";
            if (localOutput is not null) graph += $" sound. ! {audioQueue} ! local.";
        }
        _pipeline = GStreamerRuntime.CreatePipeline(graph);
        _mixer = _pipeline.GetByName("mix");
        using (var parser = _pipeline.GetByName("parser_publish"))
        using (var pad = parser.GetStaticPad("src"))
            pad.AddProbe(PadProbeType.Buffer, (_, _) =>
            {
                Frame?.Invoke(Interlocked.Increment(ref _frames));
                return PadProbeReturn.Ok;
            });
        if (settings.Audio)
        {
            using var element = _pipeline.GetByName("audio");
            _audio = new Gst.App.AppSrc(element.Handle);
            using var pad = _audio.GetStaticPad("src");
            pad.AddProbe(PadProbeType.Buffer, (_, info) =>
                info.Buffer.Offset == (ulong)Interlocked.Read(ref _source) ? PadProbeReturn.Ok : PadProbeReturn.Drop);
        }
        AddOverlay("publish", localOutput is null);
        if (localOutput is not null) AddOverlay("local", true);
        void AddOverlay(string name, bool privateView)
        {
            var renderer = new OverlayRenderer(overlays, width, height, fps);
            var calibration = new AlignmentOverlayRenderer(width, height);
            renderer.Failed += error => OverlayFailed?.Invoke(error);
            calibration.Failed += error => OverlayFailed?.Invoke(error);
            var assets = Path.GetFullPath(Path.Combine(tools, "../Assets/Standby"));
            _overlays.Add(new(_pipeline.GetByName("overlay_" + name), (canvas, duration) =>
            {
                var window = _window;
                bool available = Interlocked.Read(ref _captured) != 0 &&
                    (window == 0 || (IsWindow(window) && IsWindowVisible(window) && !IsIconic(window)));
                if (!available) canvas.Standby(assets);
                var hint = privateView ? alignment?.Invoke() ?? default : default;
                if (hint.Hint is >= 1 and <= 10)
                    calibration.Render(canvas, hint, hologram?.Invoke(), renderer.AnimationsEnabled, duration);
                else
                {
                    calibration.Render(canvas, default);
                    renderer.Render(canvas, Stopwatch.GetTimestamp(), available);
                }
                if (name == "publish")
                {
                    if (_fallback != !available) { _fallback = !available; FallbackChanged?.Invoke(!available); }
                }
            }, error => _failed.TrySetException(error)));
        }
    }
    private string CanvasCaps() => $"video/x-raw(memory:D3D11Memory),format=BGRA,width={_width},height={_height},framerate={_fps}/1,pixel-aspect-ratio=1/1";
    internal Task RunAsync(CancellationToken stop) => Task.Run(() =>
    {
        using var timer = new GStreamerRuntime.TimerResolution();
        try
        {
            if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                throw new InvalidOperationException("GStreamer could not start the stream.");
            _playing.TrySetResult();
            using var bus = _pipeline.Bus;
            while (!stop.IsCancellationRequested)
            {
                if (_failed.Task.IsCompleted) _failed.Task.GetAwaiter().GetResult();
                using var message = bus.TimedPopFiltered(50_000_000, MessageType.Error | MessageType.Eos | MessageType.Latency);
                if (message is null) continue;
                if (message.Type == MessageType.Latency) { _pipeline.RecalculateLatency(); continue; }
                if (message.Type == MessageType.Eos) throw new InvalidOperationException("The media pipeline ended unexpectedly.");
                message.ParseError(out var error, out var detail);
                var failure = new StreamPipelineException((message.Src as Element)?.Factory?.Name,
                    GStreamerRuntime.ErrorDomain(error.Domain), error.Code,
                    $"{message.Src.Name}: {error.Message} ({detail})");
                Log?.Invoke(failure.Message);
                lock (_gate)
                {
                    if (_capture is not null && (message.Src == _capture || message.Src.HasAsAncestor(_capture)))
                    { _captureFailed?.TrySetException(failure); continue; }
                }
                throw failure;
            }
            stop.ThrowIfCancellationRequested();
        }
        catch (Exception error) { _playing.TrySetException(error); throw; }
    });
    internal long BeginSource()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _captured, 0);
            return Interlocked.Increment(ref _source);
        }
    }
    internal void AddAudio(ReadOnlySpan<byte> bytes, long timestamp, long source)
    {
        lock (_gate)
        {
            if (_audio is null || _disposed || source != _source || bytes.IsEmpty) return;
            var duration = (ulong)bytes.Length * 1_000_000_000 / (ProcessAudio.SampleRate * 8);
            var now = _pipeline.CurrentRunningTime;
            if (now == ulong.MaxValue) return;
            using var buffer = new Gst.Buffer(bytes.ToArray());
            var clock = (long)((Int128)Stopwatch.GetTimestamp() * 10_000_000 / Stopwatch.Frequency);
            var age = timestamp > 0 && clock >= timestamp ? (ulong)(clock - timestamp) * 100 : duration;
            if (age > now) return;
            buffer.Pts = now - age;
            buffer.Duration = duration;
            buffer.Offset = (ulong)source;
            var result = _audio.PushBuffer(buffer);
            if (result is not FlowReturn.Ok and not FlowReturn.Flushing)
                _failed.TrySetException(new IOException($"GStreamer audio input stopped: {result}."));
        }
    }
    internal async Task CaptureAsync(CaptureSource target, nint handle, StreamSettings settings, long source, CancellationToken stop)
    {
        await _playing.Task.WaitAsync(stop);
        while (true)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var capture = CaptureVideoAsync(target, handle, settings, source, lifetime.Token);
            var changed = target.WaitForChange(handle, lifetime.Token);
            try { await await Task.WhenAny(capture, changed); }
            finally { await StreamProcess.StopAsync(lifetime, [capture, changed]); }
            stop.ThrowIfCancellationRequested();
            handle = target.CurrentHandle();
        }
    }
    private async Task CaptureVideoAsync(CaptureSource target, nint handle, StreamSettings settings, long source, CancellationToken stop)
    {
        var graph = target.CaptureElement(handle, settings) + " ! " +
            $"video/x-raw(memory:D3D11Memory),framerate={_fps}/1,pixel-aspect-ratio=1/1 ! " +
            $"d3d11convert add-borders=true border-color=0xffff03030c0c2626 ! {CanvasCaps()} ! {VideoQueue}";
        using var bin = Parse.BinFromDescription(graph, true);
        using var src = bin.GetStaticPad("src");
        using var sink = _mixer.RequestPadSimple("sink_%u");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var order = new GLib.Value((uint)1)) sink.SetProperty("zorder", order);
        lock (_gate) { _capture = bin; _captureFailed = failed; _window = target is CaptureWindow ? handle : 0; }
        src.AddProbe(PadProbeType.Buffer, (_, _) =>
        {
            if (source != Interlocked.Read(ref _source)) return PadProbeReturn.Drop;
            Interlocked.Exchange(ref _captured, Stopwatch.GetTimestamp());
            return PadProbeReturn.Ok;
        });
        try
        {
            _pipeline.Add(bin);
            if (src.Link(sink) != PadLinkReturn.Ok || !bin.SyncStateWithParent())
                throw new InvalidOperationException("GStreamer could not connect the capture source.");
            await failed.Task.WaitAsync(stop);
        }
        finally
        {
            bin.SetState(State.Null);
            src.Unlink(sink);
            _mixer.ReleaseRequestPad(sink);
            lock (_gate) { _capture = null; _captureFailed = null; }
            _pipeline.Remove(bin);
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _pipeline.SetState(State.Null);
        foreach (var overlay in _overlays) overlay.Dispose();
        _audio?.Dispose(); _mixer.Dispose(); _pipeline.Dispose();
    }
}
