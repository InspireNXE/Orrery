using System.Text;

namespace Orrery.Core.Terminal;

/// <summary>
/// One match of Find, from its first cell to its last (inclusive), in absolute rows (0 = oldest scrollback line).
/// <see cref="Line"/> is the row it starts on: rows move up as output scrolls, but the line object goes with them,
/// so it finds the match again after a new search.
/// </summary>
public readonly record struct TerminalMatch(int Row, int Column, int EndRow, int EndColumn, TerminalLine Line)
{
    /// <summary>Whether the cell is part of this match.</summary>
    public bool Covers(int row, int column) =>
        row >= Row && row <= EndRow && (row != Row || column >= Column) && (row != EndRow || column <= EndColumn);
}

/// <summary>Find in a terminal's scrollback and screen: plain text, ignoring case, across wrapped lines.</summary>
public static class TerminalSearch
{
    /// <summary>Every match of <paramref name="query"/>, oldest first, up to <paramref name="max"/>.</summary>
    public static List<TerminalMatch> Find(TerminalEmulator terminal, string query, int max)
    {
        var found = new List<TerminalMatch>();
        if (string.IsNullOrEmpty(query) || max <= 0)
        {
            return found;
        }

        lock (terminal.SyncRoot)
        {
            int total = terminal.ScrollbackCount + terminal.Rows;
            var text = new StringBuilder();
            var cells = new List<(int Row, int Column)>();
            for (int row = 0; row < total && found.Count < max; row++)
            {
                var line = terminal.LineAt(row);
                Append(line, row, text, cells);

                // A line that wrapped carries on in the next row; search once the whole of it is in.
                if (line.Wrapped && row + 1 < total)
                {
                    continue;
                }

                Search(terminal, text.ToString(), cells, query, max, found);
                text.Clear();
                cells.Clear();
            }
        }

        return found;
    }

    // Each char of the text and the cell it came from; a character outside the BMP is two chars from one cell.
    static void Append(TerminalLine line, int row, StringBuilder text, List<(int Row, int Column)> cells)
    {
        var lineCells = line.Cells;
        for (int column = 0; column < lineCells.Length; column++)
        {
            int cp = lineCells[column].CodePoint;
            if (cp == TerminalCell.WideContinuation)
            {
                continue;
            }

            if (cp == 0)
            {
                text.Append(' ');
                cells.Add((row, column));
                continue;
            }

            var chars = char.ConvertFromUtf32(cp);
            text.Append(chars);
            for (int i = 0; i < chars.Length; i++)
            {
                cells.Add((row, column));
            }
        }
    }

    static void Search(TerminalEmulator terminal, string text, List<(int Row, int Column)> cells, string query, int max, List<TerminalMatch> found)
    {
        // Ordinal, so a match is exactly as long as the query and maps back to its cells.
        for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0 && found.Count < max;
             at = at + query.Length < text.Length ? text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase) : -1)
        {
            var (row, column) = cells[at];
            var (endRow, endColumn) = cells[at + query.Length - 1];

            // A wide character's match covers both its cells.
            var endLine = terminal.LineAt(endRow);
            if (endColumn + 1 < endLine.Length && endLine.Cells[endColumn + 1].CodePoint == TerminalCell.WideContinuation)
            {
                endColumn++;
            }

            found.Add(new TerminalMatch(row, column, endRow, endColumn, terminal.LineAt(row)));
        }
    }
}
