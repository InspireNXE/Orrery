using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>What to do with a deleted host's guests: null keeps nothing (deletes them), else the folder they go to.</summary>
sealed record HostDeleteChoice(ConnectionFolder? KeepGuestsIn);

/// <summary>
/// Confirms deleting a host and asks about its guests: delete them too, or keep them as plain connections in
/// the host's folder (offered first), another folder, or the top level. Null from the dialog means cancelled.
/// </summary>
sealed class HostDeleteWindow : Window
{
    public HostDeleteWindow(ConnectionEntry host, ConnectionTree tree)
    {
        Title = "Orrery";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        int count = host.Guests.Count;
        var deleteGuests = new RadioButton { GroupName = "guests", Content = $"Delete its {count} guest{(count == 1 ? "" : "s")} too" };
        var keepGuests = new RadioButton { GroupName = "guests", Content = "Keep them, as connections in:", IsChecked = true };

        Destination = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(28, 0, 0, 0) };
        foreach (var folder in Folders(host, tree))
        {
            Destination.Items.Add(new ComboBoxItem { Content = Label(folder) + (folder == host.Parent ? "  (where it is now)" : ""), Tag = folder });
        }

        Destination.SelectedIndex = 0;
        keepGuests.IsCheckedChanged += (_, _) => Destination.IsEnabled = keepGuests.IsChecked == true;

        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var delete = new Button { Content = "Delete", Foreground = Dialogs.DangerBrush };
        cancel.Click += (_, _) => Close(null);
        delete.Click += (_, _) => Close(new HostDeleteChoice(keepGuests.IsChecked == true ? (Destination.SelectedItem as ComboBoxItem)?.Tag as ConnectionFolder : null));

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Delete host?", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"\"{host.Name}\" will be deleted. It has {count} guest{(count == 1 ? "" : "s")}.", TextWrapping = TextWrapping.Wrap },
                keepGuests,
                Destination,
                deleteGuests,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { delete, cancel } },
            },
        };
    }

    /// <summary>Where kept guests can go.</summary>
    internal ComboBox Destination { get; }

    /// <summary>The host's own folder first, then the top level and every other folder in tree order.</summary>
    internal static IEnumerable<ConnectionFolder> Folders(ConnectionEntry host, ConnectionTree tree)
    {
        var own = host.Parent!;
        yield return own;
        foreach (var folder in tree.Root.Descendants().OfType<ConnectionFolder>().Prepend(tree.Root).Where(f => f != own))
        {
            yield return folder;
        }
    }

    static string Label(ConnectionFolder folder) =>
        folder.Parent is null ? "Top level" : folder.Path is { Length: > 0 } path ? $"{path} / {folder.Name}" : folder.Name;
}
