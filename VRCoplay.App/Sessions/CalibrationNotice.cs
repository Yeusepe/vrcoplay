// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal enum CalibrationNotice { None, Local, Party }
internal static class CalibrationGuidance
{
    internal static int SharedTarget(IEnumerable<Participant> people, int hostStep)
    {
        var target = hostStep is >= 1 and <= 4 ? hostStep : 0;
        foreach (var person in people)
            if (person.Mode == SessionMode.HostGame && person.Controller is ControllerGranted &&
                person.Pointer is { Active: true, Calibrated: false, CalibrationStep: >= 1 and <= 4 } pointer)
                target = target == 0 ? pointer.CalibrationStep : Math.Min(target, pointer.CalibrationStep);
        return target;
    }
    internal static int SetupStep(SessionState? room, bool sharedVideo, int localStep) =>
        sharedVideo && room?.CalibrationTarget is >= 1 and <= 4 ? room.CalibrationTarget : localStep;
    internal static CalibrationNotice For(SessionState? room, bool host, string participantId, PointerState local)
    {
        if (room is not { HostPointerCalibrated: true })
            return CalibrationNotice.None;
        if (!host)
        {
            var self = room.Participants.FirstOrDefault(person => person.Id == participantId);
            return local is { Active: true, Calibrated: false } && self is not null && Playing(self)
                ? CalibrationNotice.Local : CalibrationNotice.None;
        }
        return room.Participants.Any(person => Playing(person) && person.Pointer is { Active: true, Calibrated: false })
            ? CalibrationNotice.Party : CalibrationNotice.None;
    }
    private static bool Playing(Participant person) =>
        person.Mode == SessionMode.LocalGame || person.Controller is ControllerGranted;
}
