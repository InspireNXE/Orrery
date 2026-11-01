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
    public const string More = "M5 12h.01 M12 12h.01 M19 12h.01";
    public const string FullScreen = "M4 9V4h5 M15 4h5v5 M20 15v5h-5 M9 20H4v-5";
    public const string File = "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5";
    public const string Up = "M12 19V5 M5 12l7-7 7 7";
    public const string Refresh = "M20 11a8 8 0 1 0-2.3 5.7 M20 4v7h-7";
    public const string Shield = "M12 3l8 3v6c0 5-3.5 8-8 9c-4.5-1-8-4-8-9V6z M12 8v5 M12 16h.01";
    public const string Alert = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v6 M12 16.5h.01";
    public const string Upload = "M12 15V3 M7 8l5-5 5 5 M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4";
    public const string Download = "M12 3v12 M7 10l5 5 5-5 M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4";
    public const string FolderPlus = "M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z M12 10v6 M9 13h6";
    public const string Rename = "M4 20h4L19 9l-4-4L4 16z M14 6l4 4";
    public const string Trash = "M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13";
}
