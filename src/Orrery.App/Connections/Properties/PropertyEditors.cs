using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>The editors the properties pane and protocols' own sections build their rows from, and how they read.</summary>
static class PropertyEditors
{
    // Fluent's switch keeps a gap on its right for On/Off text; the negative margin lines the track up
    // with the right edge of the text fields.
    public static ToggleSwitch Switch(string name) => new()
    {
        Name = name,
        OnContent = null,
        OffContent = null,
        MinWidth = 0,
        Margin = new Thickness(10, 0, -11, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A drop-down of values, each shown by its label.</summary>
    public static ComboBox Choices<T>(string name, params (T Value, string Label)[] choices) where T : notnull
    {
        var box = new ComboBox { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in choices)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        return box;
    }

    public static void Choose(ComboBox box, object value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));

    public static T? Chosen<T>(ComboBox box) where T : struct => (box.SelectedItem as ComboBoxItem)?.Tag as T?;

    /// <summary>Where an inherited value comes from, for the line under a row's editor.</summary>
    public static string Describe<T>(Resolved<T> resolved) =>
        resolved.Source is { } source ? source.Parent is null ? "From defaults for all connections" : $"From {source.Name}" : "Orrery's default";

    /// <summary>The path with the home folder written as ~, when it's under it.</summary>
    public static string WithHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return "~/" + path[(home.TrimEnd('/', '\\').Length + 1)..].Replace('\\', '/');
        }

        return path;
    }

    /// <summary>A muted line of explanation under a row or in a section.</summary>
    public static TextBlock Hint(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Dialogs.MutedBrush,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Margin = margin ?? default,
    };
}
