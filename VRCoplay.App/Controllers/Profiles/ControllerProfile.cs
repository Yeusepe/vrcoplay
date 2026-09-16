// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
public sealed record ControllerProfile(
    string Software,
    string Name,
    string Description,
    string Keywords,
    string Instructions,
    Action Install
)
{
    public bool Matches(string query)
    {
        var text = $"{Software} {Name} {Description} {Keywords}";
        return query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
internal static class ProfileCatalog
{
    public static IReadOnlyList<ControllerProfile> All { get; } =
    [
        new(
            "Dolphin",
            "Wii Remotes · Players 1–4",
            "Four Wii Remote profiles with independent VR pointing, buttons and motion.",
            "Nintendo Wii Wiimote emulator SteamVR DualShock PS4 SDL gyro gamepad absolute pointer touchpad",
            "In Settings → Controls, enable Cemuhook and DualShock and choose Preferred hand. In Dolphin’s Alternate Input Sources, enable DSU and add VRCoplay at 127.0.0.1:26760 and VRCoplay Secondary at 127.0.0.1:26761 (adjust both if you changed the Cemuhook port). In Dolphin, configure each Emulated Wii Remote and load the matching VRCoplay Player 1–4 profile. Each player uses one PS4 Controller. Your preferred hand’s trigger is Wii B. For VR pointing, install the avatar pointer on that hand and calibrate it. Leave Touch controls off.",
            DolphinProfiles.Install
        ),
        new(
            "Dolphin",
            "Wii Remote + Nunchuk · Players 1–4",
            "Independent motion for both hands, plus the Nunchuk stick and C/Z buttons.",
            "Nintendo Wii Wiimote Nunchuk nunchuck left right handed DualShock PS4 SDL boxing",
            "In Settings → Controls, enable Cemuhook and DualShock and choose Preferred hand. In Dolphin’s Alternate Input Sources, enable DSU and add VRCoplay at 127.0.0.1:26760 and VRCoplay Secondary at 127.0.0.1:26761 (adjust both if you changed the Cemuhook port). In Dolphin, load the matching VRCoplay Player 1–4 + Nunchuk profile. Each player uses one PS4 device for all buttons, sticks and pointing, plus two DSU sources for motion. DualShock motion can stay Off; these profiles use Cemuhook motion. Your preferred hand controls the Wii Remote: trigger is B and stick is the D-pad. The other hand controls the Nunchuk: grip is C and trigger is Z. If other gamepads are connected or players reconnect, check the selected devices in Dolphin. VR pointing requires the avatar pointer on your preferred hand and recalibration.",
            DolphinProfiles.InstallNunchuk
        ),
    ];
}
