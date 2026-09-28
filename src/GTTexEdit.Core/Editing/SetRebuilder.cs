using System.Buffers.Binary;
using GTTexEdit.Core.Gs;

namespace GTTexEdit.Core.Editing;

/// <summary>
/// Writes a texture set out again from what it holds, rather than patching the bytes that are there.
///
/// Everything else in this editor changes a set in place, which is why nothing has been able to change a SIZE:
/// a bigger image needs blocks that are not there, and the set's whole layout is built around where everything
/// already sits. Rebuilding lifts that - the set is taken apart into its registers, its GS memory and its clut
/// patch sets, and written back out with whatever has changed - and it reclaims the dead blocks that resizing
/// leaves behind, so a set can be edited over and over without growing every time.
///
/// What has to be true of a rebuild is that the game sees the same thing: every view decoding to the same picture
/// in every variation. That is what <see cref="Verify"/> checks, and it is checked against every set of every
/// sample car before any of this is allowed to change anything.
/// </summary>
internal static class SetRebuilder
{
    /// <summary>
    /// The set as it stands, written out again. Nothing about it changes - this is the identity of the rebuild,
    /// and the thing everything else here is measured against.
    /// </summary>
    public static byte[] Rebuild(Tex1Analysis set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Tex1Builder.Write(
            [.. set.Textures.Select(t => t.Registers)],
            set.Memory,
            set.BlockCount,
            ClutPatchSets(set),
            set.ClutAnimationOffset);
    }

    /// <summary>The clut patch sets exactly as they are, each one a count and its patch words.</summary>
    public static List<byte[]> ClutPatchSets(Tex1Analysis set)
    {
        var bodies = new List<byte[]>();
        for (int i = 0; i < set.ClutPatchSetCount; i++)
        {
            IEnumerable<Tex1Analysis.ClutPatch> patches = set.ClutPatches.Where(p => p.Set == i);
            var body = new byte[4 + patches.Count() * 4];
            BinaryPrimitives.WriteInt32LittleEndian(body, patches.Count());
            int at = 4;
            foreach (Tex1Analysis.ClutPatch patch in patches)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), patch.Raw);
                at += 4;
            }
            bodies.Add(body);
        }
        return bodies;
    }

    /// <summary>
    /// Whether a rebuilt set shows exactly what the original showed: the same number of views and variations, and
    /// every view decoding to the same pixels in every one of them. Returns what is wrong, or null when nothing is.
    /// </summary>
    /// <param name="sameBlockCount">
    /// Whether it must also fill the same amount of GS memory. True of a plain rewrite; NOT true of a repack,
    /// which reclaims what nothing reads and so is meant to end up using less.
    /// </param>
    public static string? Verify(byte[] original, byte[] rebuilt, bool sameBlockCount = true)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rebuilt);

        var before = new Tex1Reader(original);
        var after = new Tex1Reader(rebuilt);
        var beforeSet = new Tex1Analysis(original, keepIndices: false);
        var afterSet = new Tex1Analysis(rebuilt, keepIndices: false);

        if (beforeSet.TextureCount != afterSet.TextureCount)
            return $"it had {beforeSet.TextureCount} textures and now has {afterSet.TextureCount}";
        if (sameBlockCount && beforeSet.BlockCount != afterSet.BlockCount)
            return $"it filled {beforeSet.BlockCount} blocks and now fills {afterSet.BlockCount}";
        if (beforeSet.ClutPatchSetCount != afterSet.ClutPatchSetCount)
            return $"it had {beforeSet.ClutPatchSetCount} clut patch sets and now has {afterSet.ClutPatchSetCount}";

        int variations = Math.Max(1, beforeSet.ClutPatchSetCount);
        for (int texture = 0; texture < beforeSet.TextureCount; texture++)
        {
            for (int variation = 0; variation < variations; variation++)
            {
                RgbaImage was, now;
                try
                {
                    was = before.Decode(texture, variation);
                    now = after.Decode(texture, variation);
                }
                catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    return $"texture {texture} of variation {variation} will not decode any more: {e.Message}";
                }
                if (was.Width != now.Width || was.Height != now.Height)
                    return $"texture {texture} was {was.Width}x{was.Height} and is now {now.Width}x{now.Height}";
                if (!was.Pixels.AsSpan().SequenceEqual(now.Pixels))
                    return $"texture {texture} of variation {variation} does not show what it showed";
            }
        }
        return null;
    }
}
