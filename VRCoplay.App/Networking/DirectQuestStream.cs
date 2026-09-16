// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Gst;
using Task = System.Threading.Tasks.Task;
namespace VRCoplay;
internal sealed class DirectQuestStream(string input, bool audio = false)
{
    internal sealed record Status(bool Ready, string Message);
    private Status _state = new(false, "Preparing Quest streaming…");
    internal Status State => Volatile.Read(ref _state);
    internal event Action? Changed;
    internal string PublishUrl => input + "_quest";
    private void Publish(bool ready, string message)
    {
        var next = new Status(ready, message);
        if (Interlocked.Exchange(ref _state, next) != next)
            Changed?.Invoke();
    }
    internal async Task RunAsync(CancellationToken stop)
    {
        try
        {
            if (!System.Uri.TryCreate(input, UriKind.Absolute, out var source)
                || source.Scheme != "rtsp" || source.Host != "127.0.0.1"
                || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0
                || !Regex.IsMatch(source.AbsolutePath, "^/share_[A-F0-9]{32}$"))
                throw new InvalidOperationException("Quest streaming requires this PC’s shared video feed.");
            while (!stop.IsCancellationRequested)
            {
                var lastError = await Task.Run(() => PublishStream(stop), stop);
                Publish(false, $"Preparing Quest video… Retrying automatically. {lastError}");
                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Publish(false, $"Quest streaming unavailable: {error.GetBaseException().Message}");
        }
        finally
        {
            if (stop.IsCancellationRequested)
                Publish(false, "Quest streaming stopped.");
        }
    }
    private string PublishStream(CancellationToken stop)
    {
        var graph = $"rtspsrc name=source location=\"{input}\" protocols=tcp latency=0 tcp-timeout=10000000 " +
            $"rtspclientsink name=publish location=\"{PublishUrl}\" protocols=tcp latency=0 rtx-time=0 " +
            "source. ! application/x-rtp,media=video ! queue ! rtph264depay ! h264parse ! video/x-h264,stream-format=avc,alignment=au ! publish.sink_0 ";
        if (audio) graph += "source. ! application/x-rtp,media=audio ! queue ! rtpL16depay ! audioconvert ! mfaacenc bitrate=128000 ! aacparse ! audio/mpeg,mpegversion=4,stream-format=raw ! publish.sink_1";
        using var pipeline = GStreamerRuntime.CreatePipeline(graph);
        using var publish = pipeline.GetByName("publish");
        if (audio)
        {
            using var pad = publish.GetStaticPad("sink_1");
            using var payloader = ElementFactory.Make("rtpmp4gpay");
            using var value = new GLib.Value(payloader);
            pad.SetProperty("payloader", value);
        }
        try
        {
            if (pipeline.SetState(Gst.State.Playing) == StateChangeReturn.Failure)
                return "Could not start the Quest media pipeline.";
            using var bus = pipeline.Bus;
            while (!stop.IsCancellationRequested)
            {
                using var message = bus.TimedPopFiltered(50_000_000, MessageType.Error | MessageType.Eos | MessageType.Latency | MessageType.StateChanged);
                if (message is null) continue;
                if (message.Type == MessageType.StateChanged)
                {
                    message.ParseStateChanged(out _, out var state, out _);
                    if (state == Gst.State.Playing && message.Src.Name == "rtspbin" && message.Src.HasAsAncestor(publish))
                        Publish(true, "Quest video is ready.");
                    continue;
                }
                if (message.Type == MessageType.Latency) { pipeline.RecalculateLatency(); continue; }
                if (message.Type == MessageType.Eos) return "The source ended.";
                message.ParseError(out var problem, out _);
                return problem.Message;
            }
            return "";
        }
        finally { pipeline.SetState(Gst.State.Null); }
    }
}
