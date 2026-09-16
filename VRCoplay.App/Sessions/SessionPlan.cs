// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed record SessionPlan(bool LocalController, bool SendController, bool ReceiveControllers)
{
    internal static SessionPlan For(
        SessionMode mode,
        bool guest,
        bool controller,
        bool allowRequests,
        bool connected
    ) =>
        mode switch
        {
            SessionMode.ScreenSharing => new(false, false, false),
            SessionMode.LocalGame => new(controller, false, false),
            SessionMode.HostGame => new(
                !guest && controller,
                guest && connected && controller,
                !guest && connected && allowRequests
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
}
