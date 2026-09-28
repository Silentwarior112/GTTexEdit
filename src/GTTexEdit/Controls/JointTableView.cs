namespace GTTexEdit.Controls;

/// <summary>
/// The joint palette table of a buffer: one row per index value, one column per view that reads the buffer. A texel
/// picks a row and every view then shows its own column of it - which is why a texel edit reaches all of them and a
/// palette edit reaches one. Rows no texel uses are marked: they are free capacity for a new colour.
///
/// A column can be SHORTER than the table. The same memory read 8-bit by one texture and 4-bit by another is 256
/// entries to the first and 16 to the second, so the rows past a view's own palette are left blank - that view has
/// nothing there to show or to edit.
/// </summary>
internal sealed class JointTableView : Panel
{
    private const int Cell = 26;
    private const int Header = 20;
    private const int Gutter = 34;

    private uint[][] _rows = [];
    private string[] _columns = [];
    private bool[] _used = [];
    private int[] _entries = [];
    private int _row = -1, _column = -1;

    public JointTableView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A);
        ForeColor = Color.Gainsboro;
    }

    /// <summary>Raised with (row, column) when a cell is double-clicked.</summary>
    public event Action<int, int>? CellActivated;

    public event Action? SelectionChanged;

    public int SelectedRow => _row;
    public int SelectedColumn => _column;

    /// <param name="entries">How many rows each column really has; a column may hold fewer than the table.</param>
    public void SetTable(uint[][] rows, string[] columns, bool[] used, int selectedColumn, int[]? entries = null)
    {
        _rows = rows;
        _columns = columns;
        _used = used;
        _entries = entries ?? [.. columns.Select(_ => rows.Length)];
        _column = Math.Clamp(selectedColumn, 0, Math.Max(0, columns.Length - 1));
        if (_row >= rows.Length)
            _row = -1;
        AutoScrollMinSize = new Size(Gutter + columns.Length * Cell + 4, Header + rows.Length * Cell + 4);
        Invalidate();
    }

    public void Select(int row, int column)
    {
        _row = row >= 0 && row < _rows.Length ? row : -1;
        _column = Math.Clamp(column, 0, Math.Max(0, _columns.Length - 1));
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_rows.Length == 0)
            return;

        e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        using var font = new Font(Font.FontFamily, 7f);
        using var text = new SolidBrush(ForeColor);
        using var dim = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        using var frame = new Pen(Color.FromArgb(0x50, 0x50, 0x50));
        using var selected = new Pen(Color.FromArgb(0xFF, 0x80, 0x00), 2);

        for (int c = 0; c < _columns.Length; c++)
            e.Graphics.DrawString(_columns[c], font, c == _column ? text : dim, Gutter + c * Cell + 2, 4);

        for (int r = 0; r < _rows.Length; r++)
        {
            int y = Header + r * Cell;
            e.Graphics.DrawString(_used[r] ? r.ToString() : r + "*", font, _used[r] ? text : dim, 4, y + Cell / 2 - 6);
            for (int c = 0; c < _rows[r].Length; c++)
            {
                var area = new Rectangle(Gutter + c * Cell, y, Cell - 2, Cell - 2);
                if (!Holds(r, c))
                {
                    // this view reads the buffer in fewer colours than the table has rows, so it has none here
                    e.Graphics.DrawLine(frame, area.Left + 6, area.Top + area.Height / 2, area.Right - 6, area.Top + area.Height / 2);
                    continue;
                }
                Bitmaps.DrawChecker(e.Graphics, area, 5);
                using var brush = new SolidBrush(Bitmaps.ToColor(_rows[r][c]));
                e.Graphics.FillRectangle(brush, area);
                e.Graphics.DrawRectangle(frame, area);
            }
            if (r == _row && _column >= 0 && _column < _rows[r].Length && Holds(r, _column))
                e.Graphics.DrawRectangle(selected, Gutter + _column * Cell - 1, y - 1, Cell, Cell);
        }
    }

    /// <summary>Whether that column has a colour in that row at all.</summary>
    private bool Holds(int row, int column) => column < _entries.Length && row < _entries[column];

    private (int Row, int Column) HitTest(Point point)
    {
        int x = point.X - AutoScrollPosition.X, y = point.Y - AutoScrollPosition.Y;
        int row = (y - Header) / Cell, column = (x - Gutter) / Cell;
        return x < Gutter || y < Header || row < 0 || row >= _rows.Length || column < 0 || column >= _columns.Length
             || !Holds(row, column)
            ? (-1, -1)
            : (row, column);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var (row, column) = HitTest(e.Location);
        if (row >= 0)
            Select(row, column);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (row, column) = HitTest(e.Location);
        if (row >= 0)
            CellActivated?.Invoke(row, column);
    }
}
