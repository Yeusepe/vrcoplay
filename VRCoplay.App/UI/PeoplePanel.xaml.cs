// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
namespace VRCoplay;
internal enum PlayerAction { Approve, Decline, Revoke, Remove }
public sealed partial class PeoplePanel : UserControl
{
    private readonly ObservableCollection<PlayerRow> _players = [];
    private string _savedName = "";
    internal Func<string, string?>? SaveName;
    internal Func<string, PlayerAction, string?>? ActOnPlayer;
    public PeoplePanel()
    {
        InitializeComponent();
        PlayersList.ItemsSource = _players;
    }
    internal void SetSavedName(string name)
    {
        _savedName = name;
        PlayerNameBox.Text = name;
        SaveNameButton.IsEnabled = false;
        NameFeedback.Visibility = Visibility.Collapsed;
    }
    internal void Update(SessionState room, string selfId, bool host, bool canApprove, string approvalHint)
    {
        RoomSummary.Text = room.Participants.Length switch
        {
            0 => "Just you for now",
            1 => "You and 1 other player",
            var count => $"You and {count} other players",
        };
        EmptyRoom.Visibility = host && room.Participants.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ControllerHint.Text = approvalHint;
        ControllerHint.Visibility = host && room.Participants.Any(x => x.WantsController) && approvalHint.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        var people = new[] { new Participant("host", room.HostName, room.Mode) }.Concat(room.Participants).ToArray();
        for (var index = _players.Count - 1; index >= 0; index--)
            if (!people.Any(x => x.Id == _players[index].Id)) _players.RemoveAt(index);
        foreach (var person in people)
        {
            var row = _players.FirstOrDefault(x => x.Id == person.Id);
            if (row is null)
            {
                row = new(person.Id);
                _players.Add(row);
            }
            row.Update(person, selfId, host, canApprove);
        }
    }
    internal void ScrollTo(string id)
    {
        if (_players.FirstOrDefault(x => x.Id == id) is { } player) PlayersList.ScrollIntoView(player);
    }
    private void PlayerName_Changed(object sender, TextChangedEventArgs e)
    {
        if (SaveNameButton is null) return;
        SaveNameButton.IsEnabled = PlayerNameBox.Text != _savedName;
        if (SaveNameButton.IsEnabled) NameFeedback.Visibility = Visibility.Collapsed;
    }
    private void PlayerName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (SaveNameButton.IsEnabled) SavePlayerName();
    }
    private void SaveName_Click(object sender, RoutedEventArgs e) => SavePlayerName();
    private void SavePlayerName()
    {
        try
        {
            var name = PlayerNames.Normalize(PlayerNameBox.Text);
            var error = SaveName?.Invoke(name);
            if (error is not null)
            {
                Feedback(NameFeedback, error);
                return;
            }
            SetSavedName(name);
            Feedback(NameFeedback, name.Length == 0 ? "Default name saved." : "Name saved.");
        }
        catch (ArgumentException error) { Feedback(NameFeedback, error.Message); }
    }
    private void Approve_Click(object sender, RoutedEventArgs e) => Act(sender, PlayerAction.Approve);
    private void Decline_Click(object sender, RoutedEventArgs e) => Act(sender, PlayerAction.Decline);
    private void Revoke_Click(object sender, RoutedEventArgs e) => Act(sender, PlayerAction.Revoke);
    private void Remove_Click(object sender, RoutedEventArgs e) => Act(sender, PlayerAction.Remove);
    private void Act(object sender, PlayerAction action)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var error = ActOnPlayer?.Invoke(id, action);
        ActionFeedback.Visibility = Visibility.Collapsed;
        if (error is not null) Feedback(ActionFeedback, error);
        if (action is PlayerAction.Approve or PlayerAction.Decline or PlayerAction.Remove)
            PlayersList.Focus(FocusState.Programmatic);
    }
    private static void Feedback(TextBlock target, string text)
    {
        target.Text = text;
        target.Visibility = Visibility.Visible;
    }
}
public sealed class PlayerRow(string id) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; private set; } = "";
    public string Status { get; private set; } = "";
    public bool CanApprove { get; private set; }
    public Visibility ActionsVisibility { get; private set; }
    public Visibility RequestVisibility { get; private set; }
    public Visibility RevokeVisibility { get; private set; }
    public string ActionsLabel => $"Options for {Name}";
    public string ApproveLabel => $"Approve controller for {Name}";
    public string DeclineLabel => $"Decline controller for {Name}";
    internal void Update(Participant person, string selfId, bool host, bool canApprove)
    {
        var status = person.Id == "host" ? "Host" : person.Controller switch
        {
            ControllerGranted granted => person.RemotePlayRequested ? $"Remote play · Controller {granted.Slot + 1}" : $"Controller {granted.Slot + 1}",
            ControllerRequested => person.RemotePlayRequested ? "Requests remote play" : "Requests control",
            _ => person.Mode == SessionMode.LocalGame ? "Playing on their PC" : "Watching",
        };
        if (person.Id == selfId) status = $"You · {status}";
        var actions = host && person.Id != "host";
        var request = actions && person.WantsController ? Visibility.Visible : Visibility.Collapsed;
        var revoke = actions && person.Slot is not null ? Visibility.Visible : Visibility.Collapsed;
        var visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        var approve = canApprove && person.WantsController;
        if (Name == person.Name && Status == status && ActionsVisibility == visibility && RequestVisibility == request &&
            RevokeVisibility == revoke && CanApprove == approve) return;
        Name = person.Name;
        Status = status;
        ActionsVisibility = visibility;
        RequestVisibility = request;
        RevokeVisibility = revoke;
        CanApprove = approve;
        PropertyChanged?.Invoke(this, new(null));
    }
}
