namespace GTTexEdit.Core.Imaging;

/// <summary>Stacking images the ordinary way: source over destination, straight (non-premultiplied) alpha.</summary>
public static class Compositor
{
    /// <summary>
    /// Draws <paramref name="source"/> over <paramref name="target"/> with its top-left corner at (x, y), clipped
    /// to the target. Straight alpha throughout, because that is what a car palette holds: the colour under a
    /// transparent entry is meaningful (it bleeds into the edges under bilinear filtering), so it is never
    /// multiplied away.
    /// </summary>
    public static void Over(RgbaImage target, RgbaImage source, int x, int y, double opacity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0)
            return;

        int left = Math.Max(0, -x), top = Math.Max(0, -y);
        int right = Math.Min(source.Width, target.Width - x), bottom = Math.Min(source.Height, target.Height - y);

        for (int sy = top; sy < bottom; sy++)
        {
            for (int sx = left; sx < right; sx++)
            {
                int s = (sy * source.Width + sx) * 4;
                double sa = source.Pixels[s + 3] / 255.0 * opacity;
                if (sa <= 0)
                    continue;

                int d = ((y + sy) * target.Width + (x + sx)) * 4;
                if (sa >= 1)
                {
                    target.Pixels[d] = source.Pixels[s];
                    target.Pixels[d + 1] = source.Pixels[s + 1];
                    target.Pixels[d + 2] = source.Pixels[s + 2];
                    target.Pixels[d + 3] = 255;
                    continue;
                }

                double da = target.Pixels[d + 3] / 255.0;
                double outA = sa + da * (1 - sa);
                for (int c = 0; c < 3; c++)
                {
                    double value = (source.Pixels[s + c] * sa + target.Pixels[d + c] * da * (1 - sa)) / outA;
                    target.Pixels[d + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
                target.Pixels[d + 3] = (byte)Math.Clamp((int)Math.Round(outA * 255), 0, 255);
            }
        }
    }
}
