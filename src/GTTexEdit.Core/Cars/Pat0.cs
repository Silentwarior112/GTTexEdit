using System.Buffers.Binary;

namespace GTTexEdit.Core.Cars;

/// <summary>A run of bytes of the ModelSet2 that the colour patch overwrites.</summary>
public readonly record struct PatchTarget(int Offset, int Size)
{
    public int End => Offset + Size;
}

/// <summary>
/// GT4 "Pat0" colour patch: a dumb byte diff over a ModelSet2 (MDLS). Every paint overwrites the SAME list of
/// byte runs - texture palette words and material values - and differs only in the bytes it writes.
///
///   0x00 "Pat0"   0x10 u16 paint count   0x12 u16 patches per paint   (everything else up to 0x20 is zero)
///   0x20 u32[paints * patches]  offset of each patch record, paint by paint
///   then the records, in that same order: u32 target offset (MDLS-relative), u32 size, the bytes, zero-padded to 4.
///
/// Verified on all 2,420 patches of the GT4 car files: records follow the table sequentially, targets ascend and
/// never overlap, every paint lists identical targets - so <see cref="Write"/> reproduces them byte for byte.
/// </summary>
public sealed class Pat0
{
    private const uint Magic = 0x30746150; // "Pat0"
    private const int HeaderSize = 0x20;

    private readonly List<PatchTarget> _targets = [];
    private readonly List<byte[][]> _paints = []; // [paint][target] = exactly target.Size bytes

    // Which targets this tool added, so that two of them next to each other can become one. Targets the FILE
    // came with are never merged into: an untouched patch has to be written back exactly as it was read.
    private readonly HashSet<int> _added = [];

    public IReadOnlyList<PatchTarget> Targets => _targets;
    public int PaintCount => _paints.Count;

    public static bool IsPat0(ReadOnlySpan<byte> data) => data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>Parses a patch. <paramref name="data"/> may carry trailing container padding.</summary>
    public static Pat0 Read(ReadOnlySpan<byte> data)
    {
        if (!IsPat0(data))
            throw new InvalidDataException("Not a colour patch (the \"Pat0\" magic is missing).");

        int paintCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);
        int patchCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x12..]);
        if (HeaderSize + (long)paintCount * patchCount * 4 > data.Length)
            throw new InvalidDataException("Corrupt colour patch: its offset table runs past the end of the data.");

        var pat = new Pat0();
        for (int paint = 0; paint < paintCount; paint++)
        {
            var values = new byte[patchCount][];
            for (int patch = 0; patch < patchCount; patch++)
            {
                int record = BinaryPrimitives.ReadInt32LittleEndian(data[(HeaderSize + (paint * patchCount + patch) * 4)..]);
                if (record < 0 || record + 8 > data.Length)
                    throw new InvalidDataException($"Corrupt colour patch: paint {paint} patch {patch} lies outside the data.");

                var target = new PatchTarget(BinaryPrimitives.ReadInt32LittleEndian(data[record..]), BinaryPrimitives.ReadInt32LittleEndian(data[(record + 4)..]));
                if (target.Offset < 0 || target.Size < 0 || record + 8 + (long)target.Size > data.Length)
                    throw new InvalidDataException($"Corrupt colour patch: paint {paint} patch {patch} has an invalid size.");

                if (paint == 0)
                    pat._targets.Add(target);
                else if (pat._targets[patch] != target)
                    throw new InvalidDataException($"Unsupported colour patch: paint {paint} patches different bytes than paint 0 (patch {patch}).");

                values[patch] = data.Slice(record + 8, target.Size).ToArray();
            }
            pat._paints.Add(values);
        }

        for (int i = 1; i < pat._targets.Count; i++)
        {
            if (pat._targets[i].Offset < pat._targets[i - 1].End)
                throw new InvalidDataException("Unsupported colour patch: its targets overlap or are not in ascending order.");
        }
        return pat;
    }

    /// <param name="standalone">
    /// True for a race car's external ".pat" file, which ends at the last record's strict size: the final pad
    /// is not written (108 of the 806 original files end off the 4-byte grid). Embedded patches keep it.
    /// </param>
    public byte[] Write(bool standalone = false)
    {
        int recordBytes = _targets.Sum(t => 8 + ((t.Size + 3) & ~3));
        var w = new ByteWriter(capacity: HeaderSize + _paints.Count * (_targets.Count * 4 + recordBytes));
        w.WriteUInt32(Magic);
        w.WriteFill(0x0C);
        w.WriteUInt16((ushort)_paints.Count);
        w.WriteUInt16((ushort)_targets.Count);
        w.WriteFill(0x0C);

        int record = HeaderSize + _paints.Count * _targets.Count * 4;
        foreach (byte[][] _ in _paints)
        {
            foreach (PatchTarget target in _targets)
            {
                w.WriteInt32(record);
                record += 8 + ((target.Size + 3) & ~3);
            }
        }

        foreach (byte[][] values in _paints)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                w.WriteInt32(_targets[i].Offset);
                w.WriteInt32(_targets[i].Size);
                w.WriteBytes(values[i]);
                w.Align(4);
            }
        }

        int length = w.Length;
        if (standalone && _targets.Count > 0 && _paints.Count > 0)
            length -= ((_targets[^1].Size + 3) & ~3) - _targets[^1].Size;
        return w.WrittenSpan[..length].ToArray();
    }

    /// <summary>The bytes paint <paramref name="paint"/> writes over target <paramref name="target"/> (live, editable).</summary>
    public byte[] GetValue(int paint, int target) => _paints[paint][target];

    /// <summary>Index of the target containing MDLS offset <paramref name="offset"/>, or -1.</summary>
    public int FindTarget(int offset)
    {
        int low = 0, high = _targets.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (offset < _targets[mid].Offset) high = mid - 1;
            else if (offset >= _targets[mid].End) low = mid + 1;
            else return mid;
        }
        return -1;
    }

    public bool Covers(int offset, int size)
    {
        for (int i = 0; i < size; i++)
            if (FindTarget(offset + i) < 0) return false;
        return true;
    }

    /// <summary>Writes paint <paramref name="paint"/> over <paramref name="mdls"/>.</summary>
    public void Apply(int paint, Span<byte> mdls)
    {
        for (int i = 0; i < _targets.Count; i++)
        {
            PatchTarget target = _targets[i];
            if (target.End > mdls.Length)
                throw new InvalidDataException($"The colour patch writes at 0x{target.Offset:X}+{target.Size}, past the end of the model (0x{mdls.Length:X}).");
            _paints[paint][i].CopyTo(mdls[target.Offset..]);
        }
    }

    /// <summary>
    /// Makes the patch cover [<paramref name="offset"/>, +<paramref name="size"/>): every byte not covered yet
    /// becomes part of a new target, which each existing paint fills with the model's own bytes (so nothing
    /// changes visually until a paint is edited). New runs never merge into original targets, so an untouched
    /// part of the patch is written back exactly as it was read.
    /// </summary>
    public void Cover(int offset, int size, ReadOnlySpan<byte> mdls)
    {
        for (int at = offset; at < offset + size;)
        {
            int existing = FindTarget(at);
            if (existing >= 0)
            {
                at = _targets[existing].End;
                continue;
            }

            int end = at + 1;
            while (end < offset + size && FindTarget(end) < 0)
                end++;

            var target = new PatchTarget(at, end - at);
            int index = _targets.FindIndex(t => t.Offset > at);
            if (index < 0)
                index = _targets.Count;
            _targets.Insert(index, target);
            for (int paint = 0; paint < _paints.Count; paint++)
            {
                var values = _paints[paint].ToList();
                values.Insert(index, mdls.Slice(at, target.Size).ToArray());
                _paints[paint] = [.. values];
            }
            _added.Add(at);
            Merge(index);
            at = end;
        }
    }

    /// <summary>
    /// Joins a target this tool added to the ones it touches, where they were added too. A palette written back
    /// one entry at a time would otherwise become one target per WORD, and a target costs its offset, its header
    /// and its padding in every paint - 12 bytes each against 4 bytes of content. The originals cover whole runs,
    /// and so should anything written here.
    /// </summary>
    private void Merge(int index)
    {
        if (index > 0 && Joins(index - 1))
            index--;
        if (Joins(index))
            Join(index);
        if (index > 0 && Joins(index - 1))
            Join(index - 1);
    }

    private bool Joins(int first) =>
        first >= 0 && first + 1 < _targets.Count
        && _targets[first].End == _targets[first + 1].Offset
        && _added.Contains(_targets[first].Offset) && _added.Contains(_targets[first + 1].Offset);

    private void Join(int first)
    {
        PatchTarget a = _targets[first], b = _targets[first + 1];
        _added.Remove(b.Offset);
        _targets[first] = new PatchTarget(a.Offset, a.Size + b.Size);
        _targets.RemoveAt(first + 1);
        for (int paint = 0; paint < _paints.Count; paint++)
        {
            var values = _paints[paint].ToList();
            var joined = new byte[a.Size + b.Size];
            values[first].CopyTo(joined, 0);
            values[first + 1].CopyTo(joined, a.Size);
            values[first] = joined;
            values.RemoveAt(first + 1);
            _paints[paint] = [.. values];
        }
    }

    /// <summary>
    /// Says that everything from <paramref name="at"/> has moved down by <paramref name="delta"/> bytes, because
    /// the model was opened up there. A target is a byte offset, so one left behind would write its paint's
    /// colours over whatever moved into its place.
    /// </summary>
    public void Shift(int at, int delta)
    {
        var added = new HashSet<int>();
        for (int i = 0; i < _targets.Count; i++)
        {
            bool was = _added.Contains(_targets[i].Offset);
            if (_targets[i].Offset >= at)
                _targets[i] = _targets[i] with { Offset = _targets[i].Offset + delta };
            if (was)
                added.Add(_targets[i].Offset);
        }
        _added.Clear();
        foreach (int offset in added)
            _added.Add(offset);
    }

    /// <summary>
    /// Gives up [<paramref name="offset"/>, +<paramref name="size"/>): the targets covering it are split or
    /// dropped, so every byte of the range reads the model itself again, in every paint. This is what a texture
    /// editor does before it overwrites those bytes - the patch describes the bytes that WERE there, and once they
    /// change it describes nothing. Returns how many patched bytes were given up. What is left keeps ascending,
    /// non-overlapping targets, so the patch is still a valid one.
    /// </summary>
    public int Uncover(int offset, int size)
    {
        int end = offset + size, dropped = 0;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            PatchTarget target = _targets[i];
            int from = Math.Max(target.Offset, offset), to = Math.Min(target.End, end);
            if (from >= to)
                continue;
            dropped += to - from;

            // whatever the removed run leaves on either side stays covered, with its bytes
            var pieces = new List<PatchTarget>();
            if (target.Offset < from)
                pieces.Add(new PatchTarget(target.Offset, from - target.Offset));
            if (to < target.End)
                pieces.Add(new PatchTarget(to, target.End - to));

            if (_added.Remove(target.Offset))
            {
                foreach (PatchTarget piece in pieces)
                    _added.Add(piece.Offset);   // what is left of an added target is still this tool's to merge
            }
            _targets.RemoveAt(i);
            _targets.InsertRange(i, pieces);
            for (int paint = 0; paint < _paints.Count; paint++)
            {
                byte[] values = _paints[paint][i];
                var kept = _paints[paint].ToList();
                kept.RemoveAt(i);
                kept.InsertRange(i, pieces.Select(p => values.AsSpan(p.Offset - target.Offset, p.Size).ToArray()));
                _paints[paint] = [.. kept];
            }
        }
        return dropped;
    }

    /// <summary>Appends a copy of paint <paramref name="source"/> and returns its index.</summary>
    public int DuplicatePaint(int source)
    {
        _paints.Add(_paints[source].Select(v => (byte[])v.Clone()).ToArray());
        return _paints.Count - 1;
    }

    public void RemovePaint(int paint)
    {
        if (_paints.Count <= 1)
            throw new InvalidOperationException("A colour patch needs at least one paint.");
        _paints.RemoveAt(paint);
    }

    public void MovePaint(int from, int to)
    {
        byte[][] values = _paints[from];
        _paints.RemoveAt(from);
        _paints.Insert(to, values);
    }
}
