using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Orrery.App.Controls;

/// <summary>
/// A line icon drawn on a 24×24 grid, scaled to the control's size and stroked in the inherited
/// foreground colour, so it follows its button's hover and active states.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.8);

    static StrokeIcon() => AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);

    public StrokeIcon()
    {
        Width = Height = 16;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static StrokeIcon Create(string data, double size = 16, IBrush? foreground = null)
    {
        var icon = new StrokeIcon { Data = Geometry.Parse(data), Width = size, Height = size };
        if (foreground is not null)
        {
            icon.Foreground = foreground;
        }

        return icon;
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}

/// <summary>Path data for icons built in code, from the mockup. The XAML carries its own.</summary>
static class Icons
{
    public const string Monitor = "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M8 20h8 M12 16v4";
    public const string Folder = "M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z";
    public const string Plus = "M12 5v14 M5 12h14";
    public const string Close = "M6 6l12 12 M18 6L6 18";
    public const string Search = "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0 M20 20l-3.5-3.5";
    public const string Terminal = "M4 17l6-5-6-5 M12 19h8";
}
