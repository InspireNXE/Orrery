using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;

namespace Orrery.App.Sessions;

/// <summary>
/// Find in a terminal's scrollback, over its top right corner: the text to find, how many matches, older and newer,
/// and close. It searches as you type and goes to the newest match; Enter goes back through older ones.
/// </summary>
sealed class TerminalFindBar : Border
{
    readonly TerminalView _terminal;
    readonly TextBox _query = new() { PlaceholderText = "Find in terminal", Width = 240, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
    readonly Button _older = IconButton(Icons.Previous, "Older match", "Enter");
    readonly Button _newer = IconButton(Icons.Next, "Newer match", "Shift+Enter");
    readonly Button _close = IconButton(Icons.Close, "Close find", "Esc");
    readonly LiveText _spoken = new("Orrery.Terminal.Find", ScreenReader.MostRecent);

    public TerminalFindBar(TerminalView terminal)
    {
        _terminal = terminal;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 8, 18, 0);
        Padding = new Thickness(4);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, this.GetResourceObservable("OrreryRaised"));
        Bind(BorderBrushProperty, this.GetResourceObservable("OrreryControlBorder"));
        _count.Bind(TextBlock.ForegroundProperty, _count.GetResourceObservable("OrreryMuted"));

        AutomationProperties.SetName(_query, "Find in terminal");
        _query.InnerLeftContent = new StrokeIcon
        {
            Data = Geometry.Parse(Icons.Search), Width = 14, Height = 14, Foreground = Dialogs.MutedBrush, IsHitTestVisible = false,
            Margin = new Thickness(9, 0, 0, 0),
        };
        _query.InnerRightContent = _count;
        _query.TextChanged += (_, _) => _terminal.Find(_query.Text ?? string.Empty);
        _query.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    _terminal.FindStep(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : -1);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
            }
        };

        _older.Click += (_, _) => _terminal.FindStep(-1);
        _newer.Click += (_, _) => _terminal.FindStep(1);
        _close.Click += (_, _) => Close();
        _terminal.MatchesChanged += (_, _) => ShowCount();

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { _query, _older, _newer, _close, _spoken },
        };
        ShowCount();
    }

    /// <summary>Shows the bar with the terminal's selection as the text to find, if it has one, and puts the focus in it.</summary>
    public void Open(string? seed = null)
    {
        IsVisible = true;
        if (!string.IsNullOrEmpty(seed) && !seed.Contains('\n'))
        {
            _query.Text = seed;
        }
        else if (!string.IsNullOrEmpty(_query.Text))
        {
            // Opening again searches again: the output has moved on since.
            _terminal.Find(_query.Text);
        }

        _query.Focus();
        _query.SelectAll();
    }

    /// <summary>Hides the bar, clears the marks and gives the focus back to the terminal.</summary>
    public void Close()
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        _terminal.EndFind();
        _terminal.Focus();
    }

    /// <summary>What the count says, for tests.</summary>
    internal string CountText => _count.Text ?? string.Empty;

    internal TextBox Query => _query;

    void ShowCount()
    {
        int total = _terminal.Matches.Count;
        string count = total >= TerminalView.MaxMatches ? $"{TerminalView.MaxMatches:N0}+" : $"{total:N0}";
        bool empty = string.IsNullOrEmpty(_query.Text);
        _count.Text = empty ? string.Empty
            : total == 0 ? "No matches"
            : _terminal.CurrentMatch < 0 ? $"{count} found"
            : $"{_terminal.CurrentMatch + 1:N0} of {count}";
        _older.IsEnabled = _newer.IsEnabled = total > 0;
        if (IsVisible && !empty)
        {
            _spoken.Say(_count.Text);
        }
    }

    static Button IconButton(string icon, string name, string key)
    {
        var button = new Button { Content = StrokeIcon.Create(icon, 14), Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, $"{name} ({key})");
        AutomationProperties.SetName(button, name);
        return button;
    }
}
