namespace GTTexEdit.Core.Imaging;

/// <summary>
/// Quantisation for a SHARED index buffer. A car texture buffer is read by several views at once, each through a
/// palette of its own, so one texel does not have a colour - it has a vector of colours, one per view that reads
/// it. Choosing the 16 index values therefore means clustering those vectors, not the colours of any one view:
/// every class becomes one index, and each view's palette entry for that index is its own component of the class.
///
/// Views do not all reach every texel (a window reads only part of the buffer), so a component can be absent; it
/// then simply does not constrain that texel. Per-view weights let the views the user is not editing be preserved
/// as strongly as wanted. Same method as <see cref="Quantizer"/> - weighted variance median cut, then Lloyd - and
/// equally deterministic; with a single view it reduces to exactly that.
/// </summary>
internal static class JointQuantizer
{
    /// <param name="slots">Number of texels (distinct GS positions) of the buffer.</param>
    /// <param name="views">Number of views reading it.</param>
    /// <param name="colors">slots * views PNG-style colours, view-major within a slot.</param>
    /// <param name="present">slots * views: whether that view reaches that texel.</param>
    /// <param name="weights">One weight per view.</param>
    /// <param name="classes">The palette size, 16 or 256.</param>
    /// <returns>The class of every slot, and the mean colour of every (class, view).</returns>
    public static (int[] Classes, uint[][] Centres) Cluster(
        int slots, int views, uint[] colors, bool[] present, double[] weights, int classes)
    {
        int dimensions = views * 4;
        List<int> all = [.. Enumerable.Range(0, slots)];
        var boxes = new List<List<int>>();
        var means = new List<double[]>();
        var errors = new List<double>();
        boxes.Add(all);
        means.Add(Mean(all, views, colors, present, weights));
        errors.Add(Error(all, means[0], views, colors, present, weights));

        while (boxes.Count < classes)
        {
            int worst = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count > 1 && (worst < 0 || errors[i] > errors[worst]))
                    worst = i;
            }
            if (worst < 0 || errors[worst] <= 0)
                break;

            var (left, right) = Split(boxes[worst], views, colors, present, weights, dimensions);
            if (left.Count == 0 || right.Count == 0)
            {
                errors[worst] = 0; // unsplittable (every member identical): leave it alone
                continue;
            }

            boxes[worst] = left;
            means[worst] = Mean(left, views, colors, present, weights);
            errors[worst] = Error(left, means[worst], views, colors, present, weights);
            boxes.Add(right);
            means.Add(Mean(right, views, colors, present, weights));
            errors.Add(Error(right, means[^1], views, colors, present, weights));
        }

        var centres = means.ToArray();
        var classOf = new int[slots];
        Array.Fill(classOf, -1);

        for (int iteration = 0; iteration < 24; iteration++)
        {
            bool changed = false;
            for (int slot = 0; slot < slots; slot++)
            {
                int best = Nearest(slot, centres, views, colors, present, weights);
                if (best != classOf[slot])
                {
                    classOf[slot] = best;
                    changed = true;
                }
            }
            if (!changed)
                break;

            var members = new List<int>[centres.Length];
            for (int i = 0; i < members.Length; i++)
                members[i] = [];
            for (int slot = 0; slot < slots; slot++)
                members[classOf[slot]].Add(slot);
            for (int i = 0; i < centres.Length; i++)
            {
                if (members[i].Count > 0)
                    centres[i] = Mean(members[i], views, colors, present, weights);
            }
        }

        var palettes = new uint[centres.Length][];
        for (int i = 0; i < centres.Length; i++)
        {
            palettes[i] = new uint[views];
            for (int v = 0; v < views; v++)
            {
                uint color = 0;
                for (int c = 0; c < 4; c++)
                    color |= (uint)Math.Clamp((int)Math.Round(centres[i][v * 4 + c]), 0, 255) << (c * 8);
                palettes[i][v] = color;
            }
        }
        return (classOf, palettes);
    }

    private static (List<int> Left, List<int> Right) Split(List<int> box, int views, uint[] colors, bool[] present, double[] weights, int dimensions)
    {
        double[] mean = Mean(box, views, colors, present, weights);
        int axis = -1;
        double worst = -1;
        for (int d = 0; d < dimensions; d++)
        {
            double variance = 0;
            foreach (int slot in box)
            {
                if (!present[slot * views + d / 4])
                    continue;
                double delta = Channel(colors[slot * views + d / 4], d % 4) - mean[d];
                variance += weights[d / 4] * delta * delta;
            }
            if (variance > worst)
            {
                worst = variance;
                axis = d;
            }
        }
        if (axis < 0 || worst <= 0)
            return ([], []);

        // split the members that HAVE this component at their median, then let every other member follow
        // whichever of the two halves it is nearer to overall
        int view = axis / 4, channel = axis % 4;
        List<int> known = [.. box.Where(s => present[s * views + view]).OrderBy(s => Channel(colors[s * views + view], channel))];
        if (known.Count < 2)
            return ([], []);

        List<int> lowSeed = known[..(known.Count / 2)], highSeed = known[(known.Count / 2)..];
        double[] low = Mean(lowSeed, views, colors, present, weights), high = Mean(highSeed, views, colors, present, weights);

        List<int> left = [], right = [];
        foreach (int slot in box)
            (Distance(slot, low, views, colors, present, weights) <= Distance(slot, high, views, colors, present, weights) ? left : right).Add(slot);
        return (left, right);
    }

    private static double[] Mean(List<int> box, int views, uint[] colors, bool[] present, double[] weights)
    {
        var sum = new double[views * 4];
        var total = new double[views];
        foreach (int slot in box)
        {
            for (int v = 0; v < views; v++)
            {
                if (!present[slot * views + v])
                    continue;
                total[v] += weights[v];
                for (int c = 0; c < 4; c++)
                    sum[v * 4 + c] += weights[v] * Channel(colors[slot * views + v], c);
            }
        }
        for (int v = 0; v < views; v++)
            for (int c = 0; c < 4; c++)
                sum[v * 4 + c] = total[v] == 0 ? 0 : sum[v * 4 + c] / total[v];
        return sum;
    }

    private static double Error(List<int> box, double[] mean, int views, uint[] colors, bool[] present, double[] weights)
    {
        double total = 0;
        foreach (int slot in box)
            total += Distance(slot, mean, views, colors, present, weights);
        return total;
    }

    private static double Distance(int slot, double[] centre, int views, uint[] colors, bool[] present, double[] weights)
    {
        double total = 0;
        for (int v = 0; v < views; v++)
        {
            if (!present[slot * views + v])
                continue;
            uint color = colors[slot * views + v];
            for (int c = 0; c < 4; c++)
            {
                double delta = Channel(color, c) - centre[v * 4 + c];
                total += weights[v] * delta * delta;
            }
        }
        return total;
    }

    private static int Nearest(int slot, double[][] centres, int views, uint[] colors, bool[] present, double[] weights)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < centres.Length; i++)
        {
            double distance = Distance(slot, centres[i], views, colors, present, weights);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    private static int Channel(uint color, int channel) => (int)(color >> (channel * 8) & 0xFF);
}
