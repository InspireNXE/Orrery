using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Sessions;

namespace Orrery.App;

// Two sessions side by side in the session area. The current tab is one side; the other side is shown beside it until
// the split ends. Choosing another tab puts it in the side that has the focus, so the other side stays in view.
public partial class MainWindow
{
    /// <summary>How far in from the session area's left or right edge a dragged tab splits, as a share of its width.</summary>
    const double SplitZone = 0.22;

    SessionTab? _splitLeft, _splitRight;

    SessionSplitPanel SplitHost => (SessionSplitPanel)_sessionHost;

    bool IsSplit => _splitLeft is not null && _splitRight is not null;

    /// <summary>The other side of the split from <paramref name="tab"/>, or null when it isn't in one.</summary>
    SessionTab? SplitPartner(SessionTab tab) => !IsSplit ? null : tab == _splitLeft ? _splitRight : tab == _splitRight ? _splitLeft : null;

    /// <summary>
    /// Shows <paramref name="tab"/> beside <paramref name="partner"/> (the current tab by default, or the next tab
    /// when it's the current one, the previous for the last), on the side given, and makes it current.
    /// </summary>
    void SplitWith(SessionTab tab, bool onRight, SessionTab? partner = null)
    {
        var strip = StripTabs();
        if (!strip.Contains(tab))
        {
            return;
        }

        partner ??= _currentTab != tab && _currentTab is not null && strip.Contains(_currentTab) ? _currentTab
            : strip.Count > 1 ? strip[strip.IndexOf(tab) == strip.Count - 1 ? strip.Count - 2 : strip.IndexOf(tab) + 1] : null;
        if (partner is null || partner == tab)
        {
            return;
        }

        if (_fullScreen)
        {
            ExitFullScreen();
        }

        (_splitLeft, _splitRight) = onRight ? (partner, tab) : (tab, partner);
        SplitHost.Ratio = 0.5;
        SelectTab(tab);
        ConnectLog.LogInformation("Showing {Left} and {Right} side by side", _splitLeft.Origin.Title, _splitRight.Origin.Title);
    }

    /// <summary>The split shortcut: ends the split, or shows <paramref name="tab"/> on the left with the next tab on the right.</summary>
    void ToggleSplit(SessionTab tab)
    {
        if (IsSplit)
        {
            EndSplit();
        }
        else
        {
            SplitWith(tab, onRight: false);
        }
    }

    /// <summary>A tab that reconnected as a new one keeps its side of the split.</summary>
    void ReplaceInSplit(SessionTab old, SessionTab tab)
    {
        if (_splitLeft == old)
        {
            _splitLeft = tab;
        }
        else if (_splitRight == old)
        {
            _splitRight = tab;
        }
    }

    /// <summary>Back to one session at a time, showing <paramref name="keep"/> (the current tab by default).</summary>
    void EndSplit(SessionTab? keep = null, bool select = true)
    {
        if (!IsSplit)
        {
            return;
        }

        (_splitLeft, _splitRight) = (null, null);
        SplitHost.Split(null, null);
        if (select)
        {
            SelectTab(keep ?? _currentTab, chosen: false);
        }
    }

    /// <summary>
    /// Called as a tab is chosen, before the hosts are shown: a tab outside the split takes the place of the side that
    /// had the focus, so the other side stays in view.
    /// </summary>
    void PlaceInSplit(SessionTab? previous, SessionTab? tab)
    {
        if (!IsSplit)
        {
            return;
        }

        if (tab is null)
        {
            (_splitLeft, _splitRight) = (null, null);
        }
        else if (tab != _splitLeft && tab != _splitRight)
        {
            if (previous == _splitRight)
            {
                _splitRight = tab;
            }
            else
            {
                _splitLeft = tab;
            }
        }
    }

    /// <summary>Shows the split as it now is: both hosts, the line over the current side, the other tab marked in the strip.</summary>
    void ShowSplit()
    {
        foreach (var each in StripTabs())
        {
            each.Header.Classes.Set("beside", false);
        }

        if (!IsSplit)
        {
            SplitHost.Split(null, null);
            return;
        }

        SplitHost.Split(_splitLeft!.Host, _splitRight!.Host);
        SplitHost.LeftActive = _currentTab != _splitRight;
        var other = _currentTab == _splitRight ? _splitLeft : _splitRight;
        other.Host.IsVisible = true;
        other.Header.Classes.Set("beside", true);
    }

    /// <summary>The focus moved into a side of the split: that side's tab becomes current, without moving the focus again.</summary>
    void OnSessionAreaFocus(object? source)
    {
        if (!IsSplit || source is not Visual visual)
        {
            return;
        }

        var side = visual.FindAncestorOfType<Control>(includeSelf: true) is { } control
            ? new[] { _splitLeft!, _splitRight! }.FirstOrDefault(t => t.Host == control || t.Host.IsVisualAncestorOf(control))
            : null;
        if (side is not null && side != _currentTab)
        {
            SelectTab(side, focus: false);
        }
    }

    // ---- Splitting by dragging a tab to an edge of the session area ----

    Border? _splitPreview;

    /// <summary>
    /// Which side a tab dragged off the strip to <paramref name="at"/> (in the window's coordinates) would split to, or
    /// null for none: near the session area's left or right edge, with another tab to go beside.
    /// </summary>
    bool? SplitSideAt(SessionTab tab, Point at)
    {
        if (StripTabs().Count < 2 || !_sessionHost.IsEffectivelyVisible || this.TranslatePoint(at, _sessionHost) is not { } point)
        {
            return null;
        }

        var size = _sessionHost.Bounds.Size;
        if (point.Y < 0 || point.Y > size.Height || point.X < 0 || point.X > size.Width)
        {
            return null;
        }

        return point.X < size.Width * SplitZone ? false : point.X > size.Width * (1 - SplitZone) ? true : null;
    }

    /// <summary>Shades the half of the session area a dropped tab would take, or clears it for null.</summary>
    void ShowSplitPreview(bool? onRight)
    {
        if (onRight is not { } right)
        {
            if (_splitPreview is not null)
            {
                _splitPreview.IsVisible = false;
            }

            return;
        }

        if (_splitPreview is null && _sessionHost.Parent is Panel area)
        {
            _splitPreview = new Border
            {
                IsHitTestVisible = false,
                ZIndex = 10,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = Palette.Accent,
                Background = new SolidColorBrush(Palette.Accent.Color, 0.18),
                Margin = new Thickness(6),
            };
            area.Children.Add(_splitPreview);
        }

        if (_splitPreview is { } preview)
        {
            preview.HorizontalAlignment = right ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
            preview.Width = Math.Max(0, _sessionHost.Bounds.Width / 2 - 9);
            preview.IsVisible = true;
        }
    }
}
