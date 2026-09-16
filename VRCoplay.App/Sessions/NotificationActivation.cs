// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Web;
namespace VRCoplay;
internal sealed record NotificationActivation(
    string Id,
    NotificationTarget Target,
    string? RoomId,
    string? ParticipantId,
    NotificationCommand Command = NotificationCommand.Open,
    string? NotificationKey = null)
{
    internal static bool IsNotificationUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "vrcoplay" && uri.Host == "notification";
    internal static NotificationActivation? From(Uri uri)
    {
        if (!IsNotificationUri(uri)) return null;
        var args = HttpUtility.ParseQueryString(uri.Query);
        var command = NotificationCommand.Open;
        return Guid.TryParseExact(args["notification"], "N", out _) &&
            Enum.TryParse<NotificationTarget>(args["target"], out var target) && Enum.IsDefined(target) &&
            (args["command"] is null || Enum.TryParse(args["command"], out command) && Enum.IsDefined(command))
                ? new(args["notification"]!, target, args["room"], args["participant"], command, args["key"])
                : null;
    }
    internal Uri ToUri()
    {
        var query = HttpUtility.ParseQueryString("");
        query["notification"] = Id;
        query["target"] = Target.ToString();
        if (Command != NotificationCommand.Open) query["command"] = Command.ToString();
        if (NotificationKey is not null) query["key"] = NotificationKey;
        if (RoomId is not null) query["room"] = RoomId;
        if (ParticipantId is not null) query["participant"] = ParticipantId;
        return new UriBuilder("vrcoplay", "notification") { Query = query.ToString() }.Uri;
    }
}
