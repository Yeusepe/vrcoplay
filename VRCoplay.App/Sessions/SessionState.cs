// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;
namespace VRCoplay;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "access")]
[JsonDerivedType(typeof(ControllerRequested), "requested")]
[JsonDerivedType(typeof(ControllerGranted), "granted")]
internal abstract record ControllerAccess;
internal sealed record ControllerRequested : ControllerAccess;
internal sealed record ControllerGranted(int Slot, uint Id) : ControllerAccess;
internal readonly record struct PointerState(bool Active = false, bool Calibrated = false, int CalibrationStep = 0);
internal sealed record Participant(string Id, string Name, SessionMode Mode, ControllerAccess? Controller = null,
    PointerState Pointer = default, bool RemotePlayRequested = false)
{
    [JsonIgnore]
    public int? Slot => (Controller as ControllerGranted)?.Slot;
    [JsonIgnore]
    public bool WantsController => Controller is ControllerRequested;
}
internal sealed record SessionSettings(
    string? MoonlightHost = null,
    string MoonlightApp = "Desktop",
    string? WatchLink = null,
    bool InviteEnabled = false,
    bool InvitePeriodic = true,
    int InviteSeconds = 15,
    bool InviteKeepVisible = false
);
internal sealed record SessionState(string Code, SessionMode Mode, bool PinRequired, Participant[] Participants)
    : SessionCommand
{
    public string HostName { get; init; } = "Host";
    public SessionSettings Settings { get; init; } = new();
    public bool HostPointerCalibrated { get; init; }
    public int CalibrationTarget { get; init; }
    public long InviteRevision { get; init; }
    public int InviteRequestTtlMs { get; init; }
    [JsonIgnore]
    public int ControllerSlots =>
        Participants.Aggregate(
            0,
            (mask, person) => person.Slot is >= 0 and < 4 ? mask | (1 << person.Slot.Value) : mask
        );
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(RequestController), "request")]
[JsonDerivedType(typeof(ReleaseController), "release")]
[JsonDerivedType(typeof(SetPlayLocation), "location")]
[JsonDerivedType(typeof(SetPointerState), "pointer")]
[JsonDerivedType(typeof(SetPlayerName), "name")]
[JsonDerivedType(typeof(SessionState), "state")]
[JsonDerivedType(typeof(SessionNotice), "notice")]
[JsonDerivedType(typeof(ShowInvite), "invite")]
[JsonDerivedType(typeof(PairRemotePlay), "pair-remote-play")]
[JsonDerivedType(typeof(RemotePlayReady), "remote-play-ready")]
[JsonDerivedType(typeof(SetPlaybackLatency), "playback-latency")]
internal abstract record SessionCommand;
internal sealed record SetPlaybackLatency(string? WatchLink, bool? LowLatency) : SessionCommand;
internal sealed record RequestController(string? Pin, bool RemotePlay = false) : SessionCommand;
internal sealed record PairRemotePlay(Guid RequestId) : SessionCommand;
internal sealed record RemotePlayCredentials(string UniqueId, string Certificate, string PrivateKey,
    string ServerCertificate, string ServerId, int Port, int AppId)
{
    public override string ToString() => "Remote play credentials";
}
internal sealed record RemotePlayReady(Guid RequestId, RemotePlayCredentials? Credentials, string? Error = null) : SessionCommand
{
    public override string ToString() => "Remote play response";
}
internal sealed record ReleaseController : SessionCommand;
internal sealed record SetPlayLocation(SessionMode Mode) : SessionCommand;
internal sealed record SetPointerState(PointerState Pointer) : SessionCommand;
internal sealed record SetPlayerName(string Name) : SessionCommand;
internal static class PlayerNames
{
    internal const int MaxLength = 32;
    internal static string Normalize(string? name)
    {
        var value = (name ?? "").Trim().Normalize();
        if (value.Length > MaxLength || value.Any(char.IsControl))
            throw new ArgumentException("Use up to 32 characters on one line.");
        return value;
    }
    internal static string ForGuest(string id, string name) => name.Length > 0 ? name : $"Player {id[..Math.Min(4, id.Length)]}";
    internal static string ForHost(string name) => name.Length > 0 ? name : "Host";
}
internal sealed record SessionNotice(string Message) : SessionCommand;
internal sealed record ShowInvite : SessionCommand;
