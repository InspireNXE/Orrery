using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Orrery.App.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Orrery.App.Connections;

/// <summary>
/// One saved setting as a quiet row: a dot when it's set on this node, its name, and its value, muted while it comes
/// from above. A click opens the editor under it, with where the inherited value comes from and, once it's set here,
/// a way back to that. Changing the value is what sets it here; there's no separate switch for that.
/// </summary>
sealed class InheritRow
{
    static readonly IBrush DotBrush = new SolidColorBrush(Color.Parse("#4C8DFF")).ToImmutable();
    static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();
    static readonly IBrush OwnBrush = new SolidColorBrush(Color.Parse("#E6E9EE")).ToImmutable();
    static readonly IBrush InheritedBrush = new SolidColorBrush(Color.Parse("#7D8693")).ToImmutable();
    static readonly IBrush EditorBrush = new SolidColorBrush(Color.Parse("#111317")).ToImmutable();

    readonly string _label;
    readonly Control _editor;
    readonly Action _showInherited;
    readonly string _whenEmpty;
    readonly bool _canPreview;
    readonly Func<string>? _describe;
    static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#1E3A2C")).ToImmutable();
    static readonly IBrush LiveTextBrush = new SolidColorBrush(Color.Parse("#7BD3A0")).ToImmutable();
    static readonly IBrush SessionBrush = new SolidColorBrush(Color.Parse("#F0B455")).ToImmutable();

    readonly Border _live = new()
    {
        IsVisible = false,
        Background = LiveBrush,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "live", FontSize = 10, Foreground = LiveTextBrush },
    };
    readonly TextBlock _sessionValue = new() { IsVisible = false, FontSize = 11, Foreground = SessionBrush, Margin = new Thickness(14, -6, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Ellipse _dot = new() { Width = 6, Height = 6, Fill = DotBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    // The value takes only the room it needs, up to a limit, so the name beside it reads in full.
    readonly TextBlock _value = new()
    {
        FontSize = 13,
        TextAlignment = TextAlignment.Right,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 140,
        Margin = new Thickness(10, 0, 0, 0),
    };
    readonly TextBlock _hint = new() { FontSize = 11, Foreground = InheritedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _useInherited = new() { Content = "Use that instead", Classes = { "link" } };
    readonly Button _header;
    readonly Border _card;
    string _source = string.Empty, _inheritedValue = string.Empty;
    int _quiet;

    /// <param name="watchEditor">
    /// False where the editor changes itself (the credential picker, while it lists the vault): the panel calls
    /// <see cref="Edited"/> for the user's own changes instead.
    /// </param>
    /// <param name="whenEmpty">The value shown for an empty text box.</param>
    /// <param name="describe">The value to show, where the editor's own isn't it.</param>
    public InheritRow(string label, Control editor, Action showInherited, bool watchEditor = true, string whenEmpty = "None", Func<string>? describe = null)
    {
        _describe = describe;
        _label = label;
        _editor = editor;
        _showInherited = showInherited;
        _whenEmpty = whenEmpty;
        _canPreview = watchEditor;

        // The label is beside the editor only on screen; a screen reader needs it on the editor.
        AutomationProperties.SetName(editor, label);

        var name = new TextBlock { Text = label, FontSize = 13, Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(_live, 2);
        Grid.SetColumn(_value, 3);
        _header = new Button
        {
            Classes = { "setting" },
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*,Auto,Auto"), Children = { _dot, name, _live, _value } },
        };
        _header.Click += (_, _) => Expanded = !Expanded;

        _useInherited.Click += (_, _) =>
        {
            Inheriting = true;
            Quietly(_showInherited);
            ShowState();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        AutomationProperties.SetName(_useInherited, $"{label}: use the inherited value");

        // Opened, the editor is a darker band across its section's card, not a card of its own inside it.
        _card = new Border
        {
            IsVisible = false,
            Background = EditorBrush,
            Padding = new Thickness(Pane.CardPadding, 10),
            Margin = new Thickness(-Pane.CardPadding, 0, -Pane.CardPadding, 6),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { editor, new DockPanel { Children = { Pane.Docked(_useInherited, Dock.Right), _hint } } },
            },
        };

        // Every change shows in the row; one made by the user, not by the panel, sets the value here.
        void OnEditorChanged(object? sender, EventArgs e)
        {
            ShowValue();

            // A display picker says itself when the user changes it, not when it's filled in.
            if (watchEditor && !((e as RoutedEventArgs)?.Source is StyledElement source && source.FindLogicalAncestorOfType<DisplayPicker>() is not null))
            {
                Edited();
            }
        }

        // TextChanged comes later, after the panel has finished filling the fields, so the text itself is watched.
        foreach (var box in new[] { editor }.Concat(editor.GetLogicalDescendants()).OfType<TextBox>())
        {
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    OnEditorChanged(box, e);
                }
            };
        }

        editor.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnEditorChanged, handledEventsToo: true);
        editor.AddHandler(ToggleButton.IsCheckedChangedEvent, OnEditorChanged, handledEventsToo: true);

        View = new StackPanel { Children = { _header, _sessionValue, _card } };
    }

    /// <summary>Tags the row as one an open session takes up at once, from its This session tab.</summary>
    public void MarkLive()
    {
        _live.IsVisible = true;
        ToolTip.SetTip(_live, "Changes an open session at once, from its This session tab");
    }

    /// <summary>What the open session has instead of the saved value, or null when it has that.</summary>
    public void ShowSessionValue(string? value)
    {
        _sessionValue.IsVisible = value is not null;
        _sessionValue.Text = value is null ? null : $"This session: {value}";
    }

    /// <summary>Raised when the row goes from inherited to set here or back.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the panel fills its fields, so its changes aren't taken for the user's.</summary>
    public Func<bool> IsLoading { get; set; } = () => false;

    /// <summary>The value comes from above, and nothing is stored on this node.</summary>
    public bool Inheriting { get; private set; }

    public Control View { get; }

    public bool Expanded
    {
        get => _card.IsVisible;
        set => _card.IsVisible = value;
    }

    /// <summary>The user changed the value: it's set here from now on.</summary>
    public void Edited()
    {
        if (_quiet > 0 || IsLoading() || !Inheriting)
        {
            return;
        }

        Inheriting = false;
        ShowState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the editor without that counting as the user's change.</summary>
    public void Quietly(Action change)
    {
        _quiet++;
        try
        {
            change();
        }
        finally
        {
            _quiet--;
        }

        ShowValue();
    }

    /// <param name="source">Where the inherited value comes from, such as "From Lab servers".</param>
    public void Load(bool inheriting, string source)
    {
        Inheriting = inheriting;
        _source = source;

        // What the parent would give, for the line under the editor. The panel puts the node's own value in next.
        _inheritedValue = string.Empty;
        if (_canPreview)
        {
            Quietly(_showInherited);
            _inheritedValue = Summary();
        }

        ShowState();
    }

    /// <summary>Explains the setting: a tooltip over the row, and the editor's description for a screen reader.</summary>
    public void Tip(string text)
    {
        ToolTip.SetTip(View, text);
        AutomationProperties.SetHelpText(_editor, text);
    }

    void ShowState()
    {
        _dot.Opacity = Inheriting ? 0 : 1;
        _value.Foreground = Inheriting ? InheritedBrush : OwnBrush;
        _useInherited.IsVisible = !Inheriting;
        _hint.Text = Inheriting || _inheritedValue.Length == 0 ? _source : $"{_source}: {_inheritedValue}";
        ShowValue();
    }

    void ShowValue()
    {
        _value.Text = Summary();
        AutomationProperties.SetName(_header, $"{_label}: {_value.Text}{(Inheriting ? $", {_source}" : ", set here")}");
    }

    /// <summary>The editor's value in a few words: On or Off, the choice picked, or the text.</summary>
    string Summary()
    {
        if (_describe is not null)
        {
            return _describe();
        }

        var input = _editor is TextBox or ComboBox or ToggleSwitch
            ? _editor
            : _editor.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c is TextBox or ComboBox or ToggleSwitch);
        return input switch
        {
            ToggleSwitch toggle => toggle.IsChecked == true ? "On" : "Off",
            ComboBox box => (box.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty,
            TextBox box when string.IsNullOrWhiteSpace(box.Text) => _whenEmpty,
            TextBox box => string.Join(", ", box.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            _ => string.Empty,
        };
    }
}
