using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Orrery.App.Sessions;

/// <summary>
/// The session area. It holds every tab's host, one over another, and shows the current one filling it; split, it
/// shows two side by side with a divider between them to drag. The hosts never move between parents, so a remote
/// desktop or web page isn't torn down to split: only its size changes.
/// </summary>
sealed class SessionSplitPanel : Panel
{
    /// <summary>The divider's width, which takes the drag.</summary>
    public const double DividerWidth = 6;

    /// <summary>Neither side gets narrower than this share of the area.</summary>
    public const double MinRatio = 0.2;

    /// <summary>The line over the side that has the focus, so it's clear which one the toolbar and the keys are for.</summary>
    const double ActiveLine = 2;

    static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#262A31")).ToImmutable();
    static readonly IBrush DividerHover = new SolidColorBrush(Color.Parse("#3A3F48")).ToImmutable();

    readonly Border _divider = new()
    {
        Width = DividerWidth,
        Background = DividerBrush,
        Cursor = new Cursor(StandardCursorType.SizeWestEast),
        IsVisible = false,
        ZIndex = 1,
    };

    readonly Border _activeLine = new()
    {
        Height = ActiveLine,
        Background = Palette.Accent,
        IsVisible = false,
        IsHitTestVisible = false,
        ZIndex = 2,
    };

    Control? _left, _right;
    double _ratio = 0.5;
    bool _leftActive = true;
    double? _dragFrom;

    public SessionSplitPanel()
    {
        AutomationProperties.SetName(_divider, "Split divider");
        Children.Add(_divider);
        Children.Add(_activeLine);
        _divider.PointerEntered += (_, _) => _divider.Background = DividerHover;
        _divider.PointerExited += (_, _) =>
        {
            if (_dragFrom is null)
            {
                _divider.Background = DividerBrush;
            }
        };
        _divider.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_divider).Properties.IsLeftButtonPressed)
            {
                _dragFrom = e.GetPosition(this).X;
                e.Pointer.Capture(_divider);
                e.Handled = true;
            }
        };
        _divider.PointerMoved += (_, e) =>
        {
            if (_dragFrom is not null && Bounds.Width > DividerWidth)
            {
                Ratio = (e.GetPosition(this).X - DividerWidth / 2) / (Bounds.Width - DividerWidth);
            }
        };
        _divider.PointerReleased += (_, e) =>
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
            _divider.Background = DividerBrush;
        };
        _divider.PointerCaptureLost += (_, _) => _dragFrom = null;
    }

    /// <summary>Whether two hosts are side by side.</summary>
    public bool IsSplit => _left is not null && _right is not null;

    /// <summary>The left side's share of the width, from <see cref="MinRatio"/> to 1 − that.</summary>
    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = Math.Clamp(value, MinRatio, 1 - MinRatio);
            InvalidateArrange();
        }
    }

    /// <summary>Shows <paramref name="left"/> and <paramref name="right"/> side by side; nulls go back to one at a time.</summary>
    public void Split(Control? left, Control? right)
    {
        (_left, _right) = left is not null && right is not null ? (left, right) : (null, null);
        _divider.IsVisible = IsSplit;
        _activeLine.IsVisible = IsSplit;
        InvalidateArrange();
    }

    /// <summary>Which side has the focus, for the line over it.</summary>
    public bool LeftActive
    {
        get => _leftActive;
        set
        {
            _leftActive = value;
            InvalidateArrange();
        }
    }

    /// <summary>The left side's area, for a drop zone or a test.</summary>
    public Rect LeftRect => LeftIn(Bounds.Size);

    /// <summary>The right side's area.</summary>
    public Rect RightRect => RightIn(Bounds.Size);

    Rect LeftIn(Size size) =>
        new(0, ActiveLine, Math.Max(0, Math.Round((size.Width - DividerWidth) * _ratio)), Math.Max(0, size.Height - ActiveLine));

    Rect RightIn(Size size)
    {
        var left = LeftIn(size);
        double x = left.Right + DividerWidth;
        return new Rect(x, ActiveLine, Math.Max(0, size.Width - x), left.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child == _divider || child == _activeLine)
            {
                continue;
            }

            child.Arrange(!IsSplit ? new Rect(finalSize) : child == _left ? LeftIn(finalSize) : child == _right ? RightIn(finalSize) : new Rect(finalSize));
        }

        if (IsSplit)
        {
            var left = LeftIn(finalSize);
            var side = _leftActive ? left : RightIn(finalSize);
            _divider.Arrange(new Rect(left.Right, 0, DividerWidth, finalSize.Height));
            _activeLine.Arrange(new Rect(side.X, 0, side.Width, ActiveLine));
        }

        return finalSize;
    }

}
