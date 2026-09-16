// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
var builder = WebApplication.CreateBuilder(args);
_ = VRCoplay.StreamLink.Server;
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 32768);
builder.Services.AddSingleton<RoomDirectory>();
builder.Services.AddSingleton<IReleaseAccessPolicy, PublicReleaseAccess>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["RELEASE_GITHUB_REPOSITORY"]))
    builder.Services.AddHostedService<GitHubReleaseSync>();
var streamRouting = builder.Configuration.GetValue<bool>("STREAM_ROUTING");
if (streamRouting)
{
    builder.Services.AddSingleton<StreamTickets>();
    builder.Services.AddHostedService<StreamLinkResolver>();
}
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownProxies.Add(IPAddress.Parse(builder.Configuration["TRAEFIK_IP"] ?? "127.0.0.1"));
    var proxies = (builder.Configuration["FORWARDED_PROXY_IPS"] ?? "").Split(
        ',',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
    foreach (var proxy in proxies)
        o.KnownProxies.Add(IPAddress.Parse(proxy));
    o.ForwardLimit = 1 + proxies.Length;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    foreach (var (name, limit) in new[] { ("connect", 30), ("view", 120) })
        o.AddPolicy(
            name,
            c =>
                RateLimitPartition.GetFixedWindowLimiter(
                    c.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
                    _ =>
                        new()
                        {
                            PermitLimit = limit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                )
        );
});
var app = builder.Build();
app.UseForwardedHeaders();
app.MapGet("/legal/", () => Results.Redirect("/legal/index.html"));
app.UseStaticFiles();
app.UseRateLimiter();
if (streamRouting)
    StreamTickets.Map(app);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
ReleaseEndpoints.Map(app);
app.MapGet(
    "/.well-known/windows-app-web-link",
    (IConfiguration c) =>
        Results.Json(
            new[] { new { packageFamilyName = c["WINDOWS_PACKAGE_FAMILY_NAME"] ?? "", paths = new[] { "/join/*" } } }
        )
);
app.MapGet("/join", () => Results.Redirect("/alpha/index.html"));
app.MapGet(
        "/join/{code}",
        (string code, RoomDirectory rooms, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var online = rooms.Exists(code);
            return Results.Content(InvitePage.Join(code, online), "text/html", statusCode: online ? 200 : 404);
        }
    )
    .RequireRateLimiting("view");
app.MapHub<SignalingHub>("/v1/signaling").RequireRateLimiting("connect");
app.Run();
