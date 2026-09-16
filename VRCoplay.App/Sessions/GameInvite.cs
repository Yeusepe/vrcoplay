// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal static class GameInvite
{
    internal static string? RoomCode(string value, Uri coordinator)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Query.Length + uri.Fragment.Length + uri.UserInfo.Length != 0)
                return null;
            value = uri switch
            {
                { Scheme: "vrcoplay", Host: "join", IsDefaultPort: true, AbsolutePath: ['/', .. var code] } => code,
                _ when uri.GetLeftPart(UriPartial.Authority) == coordinator.GetLeftPart(UriPartial.Authority)
                    && uri.AbsolutePath.StartsWith("/join/", StringComparison.Ordinal) => uri.AbsolutePath[6..],
                _ => "",
            };
        }
        return value.Length == 6 && value.All(char.IsAsciiLetterOrDigit) ? value.ToUpperInvariant() : null;
    }
}
