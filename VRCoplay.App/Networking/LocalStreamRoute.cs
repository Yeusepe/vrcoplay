// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using WatsonWebserver;
using WatsonWebserver.Core;
namespace VRCoplay;
internal sealed class LocalStreamRoute : IDisposable
{
    private readonly Webserver _server;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _stream;
    private string _path = "game";
    private long _frame = -1,
        _received;
    private static readonly Regex Probe = new(
        @"\A/v1/probe/([A-Za-z0-9_]{1,96})/([A-Fa-f0-9]{48})\.smil\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    );
    internal LocalStreamRoute(Uri addresses, int port = StreamLink.LocalProbePort)
    {
        _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = addresses,
            Timeout = TimeSpan.FromSeconds(2),
        };
        _server = new(
            new WebserverSettings("127.0.0.1", port)
            {
                IO = new()
                {
                    MaxRequests = 16,
                    ReadTimeoutMs = 3000,
                    MaxIncomingHeadersSize = 4096,
                },
            },
            ProbeAsync
        );
        try
        {
            _server.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    internal void SetFeed(string? watchLink, string path)
    {
        lock (_gate)
        {
            _stream = watchLink is null ? null : StreamLink.StreamId(watchLink);
            _path = path;
        }
    }
    internal void ResetFrames()
    {
        lock (_gate)
        {
            _frame = -1;
            _received = 0;
        }
    }
    internal void Frame(long frame)
    {
        lock (_gate)
            if (frame > 0 && frame > _frame)
            {
                _frame = frame;
                _received = Environment.TickCount64;
            }
    }
    private async Task ProbeAsync(HttpContextBase context)
    {
        var match = Probe.Match(context.Request.Url.RawWithQuery);
        context.Response.Headers["Cache-Control"] = "no-store";
        if (context.Request.Method != WatsonWebserver.Core.HttpMethod.GET || !match.Success)
        {
            context.Response.StatusCode = 404;
            await context.Response.Send(context.Token);
            return;
        }
        var stream = match.Groups[1].Value;
        string? path = null;
        lock (_gate)
            if (_stream == stream && _received != 0 && Environment.TickCount64 - _received < 3000)
                path = _path;
        if (path is not null)
            try
            {
                using var claim = await _http.PostAsync(
                    $"/v1/view/{match.Groups[2].Value}/{stream}/{path}",
                    null,
                    context.Token
                );
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        context.Response.ContentType = "application/smil+xml";
        await context.Response.Send("<smil><head/><body/></smil>", context.Token);
    }
    public void Dispose()
    {
        ResetFrames();
        _server.Dispose();
        _http.Dispose();
    }
}
