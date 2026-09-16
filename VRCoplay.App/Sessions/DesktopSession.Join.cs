// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    private CancellationTokenSource? _joinStop;
    internal bool Joining => _joinStop is not null;
    internal event Action<string>? JoinStarting;
    internal event Action<SessionMode>? JoinLocationChosen;
    internal event Action<string, bool>? JoinFailed;
    internal void CancelJoin() => _joinStop?.Cancel();
    internal async Task<bool> JoinRoomAsync(string invite)
    {
        if (_joinStop is not null)
            return false;
        if (GameInvite.RoomCode(invite, coordinator) is not { } code)
        {
            JoinFailed?.Invoke("Enter a six-character room code or an invite link.", true);
            return false;
        }
        if (GameSession?.Session?.Code == code)
            return false;
        if (Capturing || GameSession is not null)
        {
            Show("Leave the current game session and stop sharing or playing before joining another room.", DesktopNotice.Warning);
            return false;
        }
        using var stop = _joinStop = new CancellationTokenSource();
        try
        {
            JoinStarting?.Invoke(code);
            return await ConnectGameSessionAsync(async (session, cancel) =>
            {
                var room = await session.JoinAsync(code, cancel);
                if (GameSession == session)
                    JoinLocationChosen?.Invoke(room.Mode);
            }, stop.Token);
        }
        finally
        {
            _joinStop = null;
            UpdateFlow();
        }
    }
}
