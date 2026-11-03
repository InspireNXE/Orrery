using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;

namespace Orrery.App.Recording;

/// <summary>
/// The Recordings section's main area (docs/design-session-recording.md §5.3): the player for the recording chosen
/// in the side panel, or a note saying to choose one. Showing another recording lets go of the one before.
/// </summary>
sealed class RecordingStage : Panel
{
    readonly StackPanel _empty = new()
    {
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = "Choose a recording to play", FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock
            {
                Text = "Pick one in the list on the left. Right-click it to export, show it in the folder or delete it.",
                Foreground = Dialogs.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
        },
    };

    public RecordingStage()
    {
        Background = Dialogs.PanelBrush;
        Children.Add(_empty);
    }

    /// <summary>The player's Show in folder, with the recording's path.</summary>
    public event Action<string>? ShowInFolderRequested;

    /// <summary>The player's Delete recording, with the recording's path.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>The player showing now, if any.</summary>
    public PlayerView? Player { get; private set; }

    /// <summary>Plays <paramref name="item"/>, which takes <paramref name="privateKey"/> and zeroes it when done.</summary>
    public void Play(RecordingService service, RecordingItem item, byte[] privateKey)
    {
        Close();
        Player = new PlayerView(service, item, privateKey);
        Player.ShowInFolderRequested += (_, _) => ShowInFolderRequested?.Invoke(item.Path);
        Player.DeleteRequested += (_, _) => DeleteRequested?.Invoke(item.Path);
        Children.Add(Player);
        _empty.IsVisible = false;
    }

    /// <summary>Closes the player, if it's playing <paramref name="path"/> or when no path is given.</summary>
    public void Close(string? path = null)
    {
        if (Player is null || (path is not null && !string.Equals(Player.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Children.Remove(Player);
        Player.Dispose();
        Player = null;
        _empty.IsVisible = true;
    }

    public void Pause() => Player?.Pause();
}
