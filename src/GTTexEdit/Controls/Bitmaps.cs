using System.Drawing.Imaging;
using GTTexEdit.Core;

namespace GTTexEdit.Controls;

internal static class Bitmaps
{
    /// <summary>RgbaImage ([R,G,B,A], straight alpha) to a GDI+ bitmap ([B,G,R,A]).</summary>
    public static unsafe Bitmap FromImage(RgbaImage image)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (byte* source = image.Pixels)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    byte* from = source + y * image.Width * 4, to = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < image.Width; x++, from += 4, to += 4)
                    {
                        to[0] = from[2];
                        to[1] = from[1];
                        to[2] = from[0];
                        to[3] = from[3];
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>A PNG-style colour packed 0xAABBGGRR (what the Core speaks) as a GDI+ colour.</summary>
    public static Color ToColor(uint rgba) => Color.FromArgb((int)(rgba >> 24), (int)(rgba & 0xFF), (int)(rgba >> 8 & 0xFF), (int)(rgba >> 16 & 0xFF));

    public static uint FromColor(Color color) => (uint)(color.R | color.G << 8 | color.B << 16 | color.A << 24);

    /// <summary>The light/dark checker drawn behind anything that can be transparent.</summary>
    public static void DrawChecker(Graphics g, Rectangle area, int size = 8)
    {
        using var light = new SolidBrush(Color.FromArgb(0xD8, 0xD8, 0xD8));
        using var dark = new SolidBrush(Color.FromArgb(0xA8, 0xA8, 0xA8));
        g.FillRectangle(light, area);
        for (int y = area.Top, row = 0; y < area.Bottom; y += size, row++)
        {
            for (int x = area.Left + (row % 2 == 0 ? size : 0); x < area.Right; x += size * 2)
                g.FillRectangle(dark, Rectangle.Intersect(new Rectangle(x, y, size, size), area));
        }
    }
}
