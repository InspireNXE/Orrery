namespace Orrery.Protocols.Rdp;

/// <summary>
/// How much of the desktop the server has drawn since it resized, in tiles of <see cref="Tile"/> pixels, to time a
/// resize to the picture at the new size rather than to its first update. After a resize the server redraws the whole
/// desktop, and the first update can be one small piece of it: on a link slower than a LAN that piece comes quickly
/// and the rest takes most of a second, so a resize timed to it looked fast while the redraw held up everything else.
/// A tile counts once an update covers its middle. Not thread-safe: the session uses it under its stage lock.
/// </summary>
internal sealed class RedrawCoverage
{
    internal const int Tile = 64;

    /// <summary>The share of tiles drawn that counts as the desktop shown, short of all: a corner the server leaves as it is needn't hold it up.</summary>
    internal const int EnoughPercent = 90;

    bool[] _drawn = [];
    int _width, _height, _columns, _rows, _toDraw;

    /// <summary>Whether a redraw is being followed: started and not yet covered or stopped.</summary>
    public bool Active { get; private set; }

    /// <summary>Starts following a redraw of a <paramref name="width"/> by <paramref name="height"/> desktop.</summary>
    public void Start(int width, int height)
    {
        (_width, _height) = (Math.Max(1, width), Math.Max(1, height));
        _columns = (_width + Tile - 1) / Tile;
        _rows = (_height + Tile - 1) / Tile;
        int tiles = _columns * _rows;
        if (_drawn.Length < tiles)
        {
            _drawn = new bool[tiles];
        }
        else
        {
            Array.Clear(_drawn, 0, tiles);
        }

        // The tiles still to draw, rounded up; in whole numbers, since 0.9 isn't exact in a double.
        _toDraw = Math.Max(1, (tiles * EnoughPercent + 99) / 100);
        Active = true;
    }

    public void Stop() => Active = false;

    /// <summary>
    /// Counts an update covering <paramref name="left"/> to <paramref name="right"/> and <paramref name="top"/> to
    /// <paramref name="bottom"/>, inclusive. True once, when enough of the desktop has been drawn; following stops then.
    /// </summary>
    public bool Add(int left, int top, int right, int bottom)
    {
        if (!Active)
        {
            return false;
        }

        int firstColumn = Math.Max(0, left / Tile), lastColumn = Math.Min(_columns - 1, right / Tile);
        int firstRow = Math.Max(0, top / Tile), lastRow = Math.Min(_rows - 1, bottom / Tile);
        for (int row = firstRow; row <= lastRow; row++)
        {
            int middleY = Middle(row, _height);
            if (middleY < top || middleY > bottom)
            {
                continue;
            }

            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int middleX = Middle(column, _width);
                int index = row * _columns + column;
                if (middleX < left || middleX > right || _drawn[index])
                {
                    continue;
                }

                _drawn[index] = true;
                _toDraw--;
            }
        }

        if (_toDraw > 0)
        {
            return false;
        }

        Active = false;
        return true;
    }

    /// <summary>The middle pixel of tile <paramref name="index"/> along a side <paramref name="length"/> long; the last tile can be short.</summary>
    static int Middle(int index, int length)
    {
        int start = index * Tile;
        int end = Math.Min(start + Tile, length);
        return (start + end - 1) / 2;
    }
}
