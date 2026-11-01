using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orrery.App.Controls;

public enum NoticeKind
{
    /// <summary>Plain, muted text with no icon.</summary>
    Info,

    /// <summary>A triangle with an exclamation mark, in the warning colour.</summary>
    Warning,

    /// <summary>A circle with an exclamation mark, in the danger colour.</summary>
    Error,
}

/// <summary>
/// A message with an icon that says what kind it is, so an error or a warning doesn't rely on its colour. Screen
/// readers hear "Error:" or "Warning:" before the text.
/// </summary>
public sealed class Notice : DockPanel
{
    public const string WarningIcon = "M12 3.5L2.5 20h19z M12 10v4.5 M12 17.5h.01";
    public const string ErrorIcon = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 7.5v5.5 M12 16.5h.01";

    readonly StrokeIcon _icon = new() { StrokeThickness = 2, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
    NoticeKind _kind;
    string? _prefix;

    public Notice()
        : this(NoticeKind.Error)
    {
    }

    public Notice(NoticeKind kind, string? text = null)
    {
        Message = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text };
        SetDock(_icon, Dock.Left);
        Children.Add(_icon);
        Children.Add(Message);
        Kind = kind;
    }

    /// <summary>The text part, for setting its size, weight or alignment.</summary>
    public TextBlock Message { get; }

    public string? Text
    {
        get => Message.Text;
        set
        {
            Message.Text = value;
            UpdateName();
        }
    }

    public NoticeKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            IBrush brush = value switch
            {
                NoticeKind.Error => Palette.Danger,
                NoticeKind.Warning => Palette.Warning,
                _ => Dialogs.MutedBrush,
            };
            Message.Foreground = brush;
            _icon.Foreground = brush;
            _icon.IsVisible = value != NoticeKind.Info;
            IconPath = value switch
            {
                NoticeKind.Error => ErrorIcon,
                NoticeKind.Warning => WarningIcon,
                _ => null,
            };
            _icon.Data = IconPath is null ? null : Geometry.Parse(IconPath);
            Prefix = value switch
            {
                NoticeKind.Error => "Error",
                NoticeKind.Warning => "Warning",
                _ => null,
            };
        }
    }

    /// <summary>The icon's path data, or null for none.</summary>
    public string? IconPath { get; private set; }

    /// <summary>The icon's size.</summary>
    public double IconSize
    {
        get => _icon.Width;
        set => _icon.Width = _icon.Height = value;
    }

    /// <summary>The word a screen reader hears first: "Error" or "Warning" unless set after <see cref="Kind"/>.</summary>
    public string? Prefix
    {
        get => _prefix;
        set
        {
            _prefix = value;
            UpdateName();
        }
    }

    void UpdateName() => AutomationProperties.SetName(this, Prefix is { } prefix ? $"{prefix}: {Text}" : Text);
}
