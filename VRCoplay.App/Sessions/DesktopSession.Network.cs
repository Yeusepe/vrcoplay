// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    internal DirectScreenShare? DirectShare { get; private set; }
    internal DirectQuestStream? QuestShare { get; private set; }
    internal event Action? QuestStreamChanged;
    private LocalStreamRoute? _localRoute;
    private event Action? StreamConnectionChanged;
    internal string? NetworkNotice { get; private set; }
    internal async Task SetUseDirectIpAsync(bool useDirectIp)
    {
        if (ActiveSettings is { } settings) ActiveSettings = settings with { UseDirectIp = useDirectIp };
        if (useDirectIp) StopLocalRoute();
        if (DirectShare is { } share)
            await share.SetUseDirectIpAsync(useDirectIp);
        else if (GameSession is null || IsHost)
            WatchLink = useDirectIp ? LocalVideo.PublishUrl : LocalVideo.PlayerUrl;
        StreamConnectionChanged?.Invoke();
        if (!useDirectIp && ActiveSettings?.Games == true) StartLocalRoute(WatchLink, "game");
    }
    private async Task BindStreamAddressAsync(CancellationToken stop)
    {
        var share = DirectShare;
        bool? ready = null;
        string? checkedLink = null;
        var changes = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        var probeGate = new object();
        CancellationTokenSource? probe = null;
        bool DirectOnly() => share?.UseDirectIp ?? readSettings().UseDirectIp;
        void Changed()
        {
            lock (probeGate)
                if (DirectOnly()) probe?.Cancel();
            changes.Writer.TryWrite(true);
        }
        try
        {
            StreamConnectionChanged += Changed;
            if (share is not null)
                share.Changed += Changed;
            Changed();
            await foreach (var _ in changes.Reader.ReadAllAsync(stop))
            {
                var link =
                    share is null ? WatchLink
                    : share.InternetMapped ? share.Link
                    : null;
                if (checkedLink != link)
                {
                    ready = null;
                    checkedLink = null;
                }
                Refresh();
                if (DirectOnly())
                {
                    ready = link is not null;
                    Refresh();
                    continue;
                }
                if (link is null || checkedLink == link)
                    continue;
                checkedLink = link;
                bool result;
                lock (probeGate)
                {
                    probe = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    if (DirectOnly()) probe.Cancel();
                }
                try { result = await StreamAddressCheck.ReadyAsync(link, probe.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { continue; }
                finally
                {
                    lock (probeGate)
                    {
                        probe.Dispose();
                        probe = null;
                    }
                }
                if (share is null || share.Link == link && share.InternetMapped)
                {
                    ready = result;
                    Refresh();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            StreamConnectionChanged -= Changed;
            if (share is not null)
                share.Changed -= Changed;
            AddressStopped?.Invoke();
        }
        void Refresh()
        {
            if (stop.IsCancellationRequested || DirectShare != share)
                return;
            var connecting = ready is null;
            if (share is not null)
            {
                var watchLink = share.Link;
                var current = (share.State, Active: share.InternetMapped && watchLink is not null);
                Notifications.RemoteLinkAvailable(current.Active);
                AddressAvailabilityChanged?.Invoke(current.Active);
                NetworkNotice =
                    current.Active ? null
                    : share.InternetMapped ? "Preparing the Video Player link… Reconnecting automatically."
                    : current.State?.Error is { } error ? $"Friends can’t connect yet. Retrying automatically. {error}"
                    : "Connecting friends… The Video Player link will be ready shortly.";
                var link = current.Active ? watchLink! : "";
                if (WatchLink != link)
                {
                    WatchLink = link;
                    if (ActiveSettings?.Games == true)
                    {
                        StartLocalRoute(link.Length == 0 ? null : link, "game");
                        PublishWatchLink();
                    }
                }
                if (stop.IsCancellationRequested || share.State != current.State)
                    return;
                connecting = current.Active ? NetworkNotice is null && ready is null : current.State?.Error is null;
            }
            AddressChanged?.Invoke(ready, connecting);
        }
    }
    internal static void PrepareNetwork(StreamSettings settings)
    {
        if (settings.Audience == 1 && settings.DirectAddress.Length == 0)
            _ = InternetHosting.DiscoverAsync();
    }
    private void StartLocalRoute(string? link, string path)
    {
        if (DirectShare?.UseDirectIp ?? readSettings().UseDirectIp) return;
        try
        {
            _localRoute ??= new(coordinator);
            _localRoute.SetFeed(link, path);
        }
        catch (Exception e)
        {
            NetworkNotice = $"Direct viewing is available, but automatic local viewing could not start: {e.Message}";
        }
    }
    private void StopLocalRoute() => Interlocked.Exchange(ref _localRoute, null)?.Dispose();
    private void PublishWatchLink()
    {
        if (!IsHost || GameSession is not { Session: not null } gameSession)
            return;
        var link = DirectShare is { InternetMapped: true } share ? share.RegisteredLink : null;
        if (gameSession.Session!.Settings.WatchLink == link || link is not null && StreamLink.StreamId(link) is null)
            return;
        try
        {
            gameSession.Configure(gameSession.Session!.Settings with { WatchLink = link });
        }
        catch (Exception e)
        {
            NetworkNotice =
                $"Video is running. The game room could not update its watch link: {e.GetBaseException().Message}";
        }
    }
}
