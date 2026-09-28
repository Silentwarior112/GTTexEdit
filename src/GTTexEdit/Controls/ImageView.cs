using System.Drawing.Drawing2D;
using GTTexEdit.Core;

namespace GTTexEdit.Controls;

/// <summary>
/// Shows one texture as large as fits, in whole-number zoom steps with hard pixel edges, over a transparency
/// checker. Clicking a pixel reports its colour, so the owner can point out which palette entry painted it.
/// </summary>
internal sealed class ImageView : Control
{
    private RgbaImage? _image;
    private Bitmap? _bitmap;
    private Rectangle _drawn;
    private Point? _dragFrom;

    public ImageView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(0x30, 0x30, 0x30);
    }

    /// <summary>Raised with the texel that was clicked.</summary>
    public event Action<int, int>? PixelClicked;

    /// <summary>Raised while the left button drags, with how far it moved in texels since the last report.</summary>
    public event Action<int, int>? PixelDragged;

    /// <summary>An outline to draw over the image, in texels - what is being placed.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle? Highlight { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public RgbaImage? Image
    {
        get => _image;
        set
        {
            _image = value;
            _bitmap?.Dispose();
            _bitmap = value is null ? null : Bitmaps.FromImage(value);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_bitmap is null)
            return;

        // Whole-number zoom when enlarging keeps every texel square; shrink freely when the texture is too big.
        float zoom = Math.Min((ClientSize.Width - 16f) / _bitmap.Width, (ClientSize.Height - 16f) / _bitmap.Height);
        zoom = zoom >= 1 ? MathF.Floor(zoom) : Math.Max(zoom, 0.05f);
        int width = Math.Max(1, (int)(_bitmap.Width * zoom)), height = Math.Max(1, (int)(_bitmap.Height * zoom));
        _drawn = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);

        Bitmaps.DrawChecker(e.Graphics, _drawn);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_bitmap, _drawn);

        if (Highlight is Rectangle area && _image is not null)
        {
            float scale = _drawn.Width / (float)_image.Width;
            var outline = new Rectangle(
                _drawn.Left + (int)(area.X * scale), _drawn.Top + (int)(area.Y * scale),
                Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
            using var shadow = new Pen(Color.FromArgb(0xC0, Color.Black)) { DashStyle = DashStyle.Dash };
            using var pen = new Pen(Color.FromArgb(0xFF, 0x80, 0x00)) { DashStyle = DashStyle.Dash, DashOffset = 3 };
            e.Graphics.DrawRectangle(shadow, outline);
            e.Graphics.DrawRectangle(pen, outline);
        }
    }

    private Point? TexelAt(Point location)
    {
        if (_image is null || !_drawn.Contains(location))
            return null;
        return new Point(Math.Min(_image.Width - 1, (location.X - _drawn.Left) * _image.Width / _drawn.Width),
                         Math.Min(_image.Height - 1, (location.Y - _drawn.Top) * _image.Height / _drawn.Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is not Point from || e.Button != MouseButtons.Left || TexelAt(e.Location) is not Point to)
            return;
        if (to != from)
        {
            _dragFrom = to;
            PixelDragged?.Invoke(to.X - from.X, to.Y - from.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragFrom = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || TexelAt(e.Location) is not Point texel)
            return;
        _dragFrom = texel;
        PixelClicked?.Invoke(texel.X, texel.Y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap?.Dispose();
        base.Dispose(disposing);
    }
}
