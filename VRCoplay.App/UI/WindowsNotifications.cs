// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Windows.AppNotifications;
namespace VRCoplay;
internal sealed class WindowsNotifications : IDisposable
{
    internal const string Group = "vrcoplay-session";
    private AppNotificationManager? _manager;
    private Task _operations = Task.CompletedTask;
    internal WindowsNotifications()
    {
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            var manager = AppNotificationManager.Default;
            if (Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().IsPackaged)
                manager.Register();
            else
                manager.Register("VRCoplay", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "VRCoplay.ico")));
            _manager = manager;
        }
        catch (Exception error) { Trace.TraceWarning($"Windows notifications unavailable: {error.Message}"); }
    }
    internal Task ShowAsync(SessionNotification notice, Func<bool> shouldShow) => Enqueue(async manager =>
    {
        if (!shouldShow()) return;
        await manager.RemoveByTagAndGroupAsync(Tag(notice.Key), Group);
        if (!shouldShow()) return;
        var activation = new NotificationActivation(Guid.NewGuid().ToString("N"), notice.Target, notice.RoomId,
            notice.ParticipantId, NotificationKey: notice.Key);
        var uri = activation.ToUri();
        var content = new XElement("toast", new XAttribute("activationType", "protocol"), new XAttribute("launch", uri.AbsoluteUri),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", notice.Title), new XElement("text", notice.Message))),
            notice.Actions.Length == 0 ? null : new XElement("actions", notice.Actions.Select(action =>
                new XElement("action", new XAttribute("content", action.Text),
                    new XAttribute("activationType", "protocol"),
                    new XAttribute("arguments", (activation with { Command = action.Command }).ToUri().AbsoluteUri)))));
        var notification = new AppNotification(content.ToString(SaveOptions.DisableFormatting));
        notification.Tag = Tag(notice.Key);
        notification.Group = Group;
        notification.Expiration = DateTimeOffset.UtcNow.AddMinutes(30);
        notification.ExpiresOnReboot = true;
        manager.Show(notification);
    });
    internal Task RemoveAsync(string key) => Enqueue(async manager => await manager.RemoveByTagAndGroupAsync(Tag(key), Group));
    internal Task ClearAsync() => Enqueue(async manager => await manager.RemoveByGroupAsync(Group));
    private Task Enqueue(Func<AppNotificationManager, Task> operation)
    {
        var previous = _operations;
        return _operations = RunAsync();
        async Task RunAsync()
        {
            await previous;
            try { if (_manager is { } manager) await operation(manager); }
            catch (Exception error) { Trace.TraceWarning($"Windows notification operation failed: {error.Message}"); }
        }
    }
    private static string Tag(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _manager, null) is not { } manager) return;
        try { manager.Unregister(); }
        catch (Exception error) { Trace.TraceWarning($"Notification cleanup failed: {error.Message}"); }
    }
}
