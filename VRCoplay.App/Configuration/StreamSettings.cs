// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace VRCoplay;
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record StreamSettings
{
    public string PlayerName { get; set; } = "";
    [JsonIgnore]
    [Range(0, 3)]
    public int DisplayLanguage { get; set; }
    public bool Audio { get; set; } = true;
    public bool QuietLocalAudio { get; set; } = true;
    public bool Controller { get; set; } = true;
    public bool Cemuhook { get; set; } = true;
    [Range(1024, 65534)]
    public int CemuhookPort { get; set; } = CemuhookServer.DefaultPort;
    public bool DualShock { get; set; } = true;
    [Range(0, 2)]
    public int DualShockMotionHand { get; set; }
    [Range(0, 1)]
    public int ControllerHand { get; set; }
    public int World { get; set; }
    public bool Cursor { get; set; } = true;
    [Range(1, 240)]
    public int? Fps { get; set; }
    [Range(1, int.MaxValue)]
    public int? Width { get; set; }
    [Range(1, int.MaxValue)]
    public int? Height { get; set; }
    [Range(0, int.MaxValue)]
    public int CropLeft { get; set; }
    [Range(0, int.MaxValue)]
    public int CropTop { get; set; }
    [Range(0, int.MaxValue)]
    public int CropRight { get; set; }
    [Range(0, int.MaxValue)]
    public int CropBottom { get; set; }
    public int Encoder { get; set; }
    [Range(0, 51)]
    public int? Quality { get; set; }
    public string EncoderProperties { get; set; } = "";
    public int Audience { get; set; } = 1;
    public bool UseDirectIp { get; set; }
    public bool StreamerMode { get; set; }
    public bool QuestStreaming { get; set; }
    public bool RequirePin { get; set; } = true;
    public bool AllowRequests { get; set; } = true;
    public bool Pointer { get; set; }
    public bool InviteOverlay { get; set; }
    public bool InvitePeriodic { get; set; } = true;
    [Range(5, 120)]
    public int InviteSeconds { get; set; } = 15;
    public bool InviteKeepVisible { get; set; }
    public bool WarningOverlay { get; set; } = true;
    public bool Games { get; set; }
    public int GameMode { get; set; }
    public string MoonlightHost { get; set; } = "";
    public string MoonlightApp { get; set; } = "";
    public string DirectAddress { get; set; } = "";
    public int DirectPort { get; set; } = 8554;
    [JsonIgnore]
    public int Activity
    {
        get => Games ? 1 : 0;
        set => Games = value == 1;
    }
    internal StreamSettings Snapshot() =>
        this with
        {
            MoonlightHost = this.MoonlightHost.Trim(),
            MoonlightApp = this.MoonlightApp.Trim(),
            DirectAddress = this.DirectAddress.Trim(),
        };
    internal void Validate(CaptureSource target)
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if ((this.Width is null) != (this.Height is null))
            throw new InvalidOperationException("Set both output width and height, or leave both automatic.");
        if (this.Width is int width && (width % 2 != 0 || this.Height % 2 != 0))
            throw new InvalidOperationException("Output width and height must be even numbers.");
        var source = target.CaptureSize();
        if (
            (long)this.CropLeft + this.CropRight >= source.Width
            || (long)this.CropTop + this.CropBottom >= source.Height
        )
            throw new InvalidOperationException("Crop values must leave part of the source image visible.");
    }
    internal SessionMode Mode =>
        !Games ? SessionMode.ScreenSharing
        : GameMode == 1 ? SessionMode.LocalGame
        : SessionMode.HostGame;
}
