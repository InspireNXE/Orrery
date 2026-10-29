namespace Orrery.Core.Terminal;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
}

/// <summary>
/// A cell colour: the terminal default, one of the 256 palette entries, or 24-bit RGB.
/// The renderer turns it into a real colour; the emulator never needs to.
/// </summary>
public readonly record struct TerminalColor(uint Value)
{
    const uint RgbFlag = 0x1000000;

    public static readonly TerminalColor Default = new(0);

    public static TerminalColor Palette(int index) => new((uint)(Math.Clamp(index, 0, 255) + 1));

    public static TerminalColor Rgb(int r, int g, int b) =>
        new(RgbFlag | (uint)((Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255)));

    public bool IsDefault => Value == 0;
    public bool IsPalette => Value is >= 1 and <= 256;
    public int PaletteIndex => (int)Value - 1;
    public bool IsRgb => (Value & RgbFlag) != 0;
    public uint Rgb24 => Value & 0xFFFFFF;
}

public readonly record struct CellStyle(TerminalColor Foreground, TerminalColor Background, CellFlags Flags)
{
    public static readonly CellStyle Default = new(TerminalColor.Default, TerminalColor.Default, CellFlags.None);

    /// <summary>What erased cells get: the current background, nothing else (xterm's "background colour erase").</summary>
    public CellStyle ForErase => new(TerminalColor.Default, Background, CellFlags.None);
}

public struct TerminalCell
{
    /// <summary>Marks the right half of a double-width character.</summary>
    public const int WideContinuation = -1;

    /// <summary>A Unicode code point; 0 for an empty cell (drawn as a space).</summary>
    public int CodePoint;
    public CellStyle Style;

    public TerminalCell(int codePoint, CellStyle style)
    {
        CodePoint = codePoint;
        Style = style;
    }

    public readonly bool IsBlank => CodePoint is 0 or ' ';
}

public sealed class TerminalLine
{
    public TerminalLine(int columns, CellStyle fill)
    {
        Cells = new TerminalCell[columns];
        Fill(0, columns, fill);
    }

    public TerminalCell[] Cells { get; private set; }

    /// <summary>True when the text runs on to the next line because it hit the edge, not because of a newline.</summary>
    public bool Wrapped { get; set; }

    public int Length => Cells.Length;

    public void Fill(int from, int to, CellStyle style)
    {
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            Cells[i] = new TerminalCell(0, style);
        }
    }

    public void Resize(int columns, CellStyle fill)
    {
        if (columns == Cells.Length)
        {
            return;
        }

        var cells = Cells;
        Array.Resize(ref cells, columns);
        for (int i = Cells.Length; i < columns; i++)
        {
            cells[i] = new TerminalCell(0, fill);
        }

        Cells = cells;
    }

    public TerminalLine Clone()
    {
        var copy = new TerminalLine(0, CellStyle.Default) { Wrapped = Wrapped };
        copy.Cells = (TerminalCell[])Cells.Clone();
        return copy;
    }

    /// <summary>The line's text with trailing blanks trimmed.</summary>
    public string Text(int from = 0, int to = int.MaxValue)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            var cp = Cells[i].CodePoint;
            if (cp == TerminalCell.WideContinuation)
            {
                continue;
            }

            builder.Append(cp == 0 ? " " : char.ConvertFromUtf32(cp));
        }

        return builder.ToString().TrimEnd(' ');
    }
}
