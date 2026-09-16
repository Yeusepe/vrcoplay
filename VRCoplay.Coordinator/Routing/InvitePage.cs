// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using VRCoplay;
internal static class InvitePage
{
    private const string AppHelp = "<details><summary>App didn’t open?</summary><p>Open VRCoplay once, then try the invite again.</p></details>";
    internal static string Watch(string encoded, string sources, string? code = null, string? watchLink = null) => Layout(code is null ? "Watch in VRChat®" : "Join game",
        code is null ? Hero("Watch in VRChat®", "Copy the link, then paste it into a VRChat® Video Player.", Copy(watchLink ?? StreamLink.ServerUrl("/watch/" + encoded), "Video Player link", "primary")) : Game(code),
        (code is null ? "" : $$"""
        <section class="watch" aria-labelledby="watch-title">
          <div><h2 id="watch-title">Watch on a VRChat® Video Player</h2><p>Paste this link into a VRChat® Video Player. No app needed.</p></div>
          {{Copy(watchLink ?? StreamLink.ServerUrl("/watch/" + encoded), "Video Player link")}}
        </section>
        """) + $$"""
        <section><h2>Stream privacy</h2><p>This link connects viewers directly to the host’s PC. VRCoplay’s servers never receive the video. Viewers can discover the host’s IP address. Share only with people you trust. Avoid public worlds.</p></section>
        <script>if(window.jwplayer){jwplayer('vrcoplay').setup({{sources}});}</script>
        """, code is null ? "" : AppHelp);
    internal static string Join(string code, bool online) => Layout(online ? "Join game" : "Room unavailable",
        Game(code.Length == 6 && code.All(char.IsAsciiLetterOrDigit) ? code.ToUpperInvariant() : null, online),
        """
        <section class="install" aria-labelledby="install-title">
          <h2 id="install-title">Install VRCoplay</h2>
          <p>Get the Windows alpha to join the game. This is an early version for testing.</p>
          <a class="button secondary" href="/alpha/"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/></svg>Install VRCoplay alpha</a>
          <p class="install-next">Follow the setup steps, then return to this invite to join.</p>
        </section>
        """, AppHelp);
    private static string Game(string? code, bool online = true)
    {
        var canJoin = code is not null && online;
        return Hero(canJoin ? "Join game" : "Room unavailable", canJoin ? "Join in VRCoplay, then choose Play." : "Ask the host for a new invite.",
            $"<a class=button href=\"{(canJoin ? "vrcoplay://join/" + code : "vrcoplay://open")}\">{(canJoin ? "Join in VRCoplay" : "Open VRCoplay")}</a>" +
            (canJoin ? Copy(StreamLink.ServerUrl("/join/" + code), "invite") : ""), code);
    }
    private static string Hero(string title, string hint, string actions, string? code = null) => $$"""
        <section class="invite" aria-labelledby="room-title">
          <div class="intro">
            <h1 id="room-title">{{title}}</h1>
            <p class="lead">{{hint}}</p>
            <div class="actions">{{actions}}</div>
          </div>
          {{(code is null ? "" : $$"""
          <section class="room" aria-label="Game room">
            <div class="room-heading">Room code</div>
            <p class="room-code">{{string.Concat(code.Select(c => $"<span>{c}</span>"))}}</p>
            {{Copy(code, "code")}}
          </section>
          """)}}
        </section>
        """;
    private static string Copy(string value, string label, string appearance = "secondary") => $"<button class={appearance} aria-label=\"Copy {label}\" data-copy=\"{WebUtility.HtmlEncode(value)}\" data-label=\"{label}\" hidden><span class=copy-label>Copy {label}</span></button>";
    private static string Layout(string title, string content, string watching, string help = "") => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="no-referrer"><meta name="robots" content="noindex, nofollow">
        <title>VRCoplay · {{title}}</title><link rel="icon" href="/brand/icon.svg" type="image/svg+xml"><link rel="stylesheet" href="/invite.css"><script src="/invite.js" type="module"></script></head>
        <body><main><header><img class="brand" src="/brand/logo.svg" width="224" height="42" alt="VRCoplay"></header>
        {{content}}
        <footer>
          <div class="guidance">{{watching}}</div>
          {{help}}
          <p>© 2026 YUCP Studio · VRCoplay · GPL v3 or later · No warranty. <a href="/legal/">Copyright and licenses</a></p>
        </footer>
        <p id="copy-status" class="sr-only" role="status"></p><div id="copy-fallback" hidden><label for="copy-value">Select and copy</label><input id="copy-value" readonly></div>
        </main></body></html>
        """;
}
