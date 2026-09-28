namespace GTTexEdit.Controls;

/// <summary>What the grid needs to know about one palette entry.</summary>
internal readonly record struct PaletteCell(uint Color, bool Editable, bool Patched);

/// <summary>
/// A palette as swatches, 16 to a row. The frame of a swatch says whether the colour patch already recolours the
/// entry (white) or not yet (dark); an entry that cannot be edited is hatched. Click selects, double-click edits.
/// </summary>
internal sealed class PaletteGrid : Control
{
    private const int Columns = 16;
    private PaletteCell[] _cells = [];
    private int _selected = -1;

    public PaletteGrid()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event Action? SelectionChanged;
    public event Action<int>? EntryActivated;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedEntry
    {
        get => _selected;
        set
        {
            int entry = value >= 0 && value < _cells.Length ? value : -1;
            if (entry == _selected)
                return;
            _selected = entry;
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    public void SetCells(PaletteCell[] cells)
    {
        _cells = cells;
        if (_selected >= cells.Length)
            _selected = -1;
        Invalidate();
    }

    private int CellSize => Math.Max(8, Math.Min(ClientSize.Width / Columns, 40));

    /// <summary>Height the grid wants for its current cells at the current width.</summary>
    public int PreferredGridHeight => (_cells.Length + Columns - 1) / Columns * CellSize + 1;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int size = CellSize;
        using var patched = new Pen(Color.White);
        using var unpatched = new Pen(Color.FromArgb(0x40, 0x40, 0x40));
        using var selected = new Pen(Color.FromArgb(0xFF, 0x80, 0x00), 3);
        using var hatch = new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.DiagonalCross, Color.Gray, Color.FromArgb(0x30, 0x30, 0x30));

        for (int i = 0; i < _cells.Length; i++)
        {
            var area = new Rectangle(i % Columns * size, i / Columns * size, size, size);
            var inner = Rectangle.Inflate(area, -2, -2);
            if (_cells[i].Editable)
            {
                Bitmaps.DrawChecker(e.Graphics, inner, 4);
                using var brush = new SolidBrush(Bitmaps.ToColor(_cells[i].Color));
                e.Graphics.FillRectangle(brush, inner);
            }
            else
            {
                e.Graphics.FillRectangle(hatch, inner);
            }
            e.Graphics.DrawRectangle(_cells[i].Patched ? patched : unpatched, area.X + 1, area.Y + 1, size - 3, size - 3);
        }

        if (_selected >= 0)
            e.Graphics.DrawRectangle(selected, _selected % Columns * size + 1, _selected / Columns * size + 1, size - 3, size - 3);
    }

    private int HitTest(Point point)
    {
        int size = CellSize, column = point.X / size, row = point.Y / size;
        int entry = row * Columns + column;
        return column < Columns && entry >= 0 && entry < _cells.Length ? entry : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int entry = HitTest(e.Location);
        if (entry >= 0)
            SelectedEntry = entry;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int entry = HitTest(e.Location);
        if (entry >= 0 && _cells[entry].Editable)
            EntryActivated?.Invoke(entry);
    }
}
