// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CliWrap;
namespace VRCoplay;
internal static class LocalVideo
{
    internal static string PlayerUrl => StreamLink.Local;
    internal const string PublishUrl = "rtsp://127.0.0.1:8554/game";
    internal static Command MediaCommand(string tools, int port = 8554, bool share = false) =>
        Cli.Wrap(Path.Combine(tools, "mediamtx.exe"))
            .WithArguments([Path.Combine(tools, share ? "screenshare.yml" : "mediamtx.yml")])
            .WithEnvironmentVariables(env =>
            {
                foreach (
                    var key in Environment
                        .GetEnvironmentVariables()
                        .Keys.Cast<string>()
                        .Where(key => key.StartsWith("MTX_", StringComparison.OrdinalIgnoreCase))
                )
                    env.Set(key, null);
                env.Set("MTX_RTSPADDRESS", $"{(share ? "0.0.0.0" : "127.0.0.1")}:{port}");
            });
}
