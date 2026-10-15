namespace PictureSharp.Domain.Metadata;

internal sealed partial class ExifDocument
{
    public byte[]? Serialize()
    {
        if (_directories.Count == 0 || !_directories.Any(d => !d.Removed && d.Entries.Any(e => !e.Removed && e.Children is null))) return null;
        int[] offsets = new int[_directories.Count];
        long end = Math.Max(8, _source.Length);
        for (int i = 0; i < offsets.Length; i++)
        {
            if (_directories[i].Removed) continue;
            end = (end + 1) & ~1L;
            if (end > Array.MaxLength) throw new NotSupportedException("EXIF exceeds the managed buffer limit.");
            offsets[i] = (int)end;
            int count = _directories[i].Entries.Count(e => !e.Removed);
            if (count > ushort.MaxValue) throw new NotSupportedException("Too many EXIF tags.");
            end += 6L + (12L * count);
        }
        long valuesStart = end;
        foreach (Entry entry in _directories.Where(d => !d.Removed).SelectMany(d => d.Entries).Where(e => !e.Removed))
        {
            int length = entry.Children is { } children ? children.Length * 4 : entry.Replacement?.Length ?? 0;
            if (length > 4) end = ((end + 1) & ~1L) + length;
        }
        if (end > Array.MaxLength) throw new NotSupportedException("EXIF exceeds the managed buffer limit.");
        byte[] result = new byte[(int)end];
        _source.CopyTo(result, 0);
        (_big ? "MM"u8 : "II"u8).CopyTo(result);
        Write16(result.AsSpan(2), 42);
        Write32(result.AsSpan(4), (uint)offsets[0]);

        // Scrub removed values, including GPS data, rather than merely making the bytes unreachable.
        var retained = _directories.Where(d => !d.Removed).SelectMany(d => d.Entries)
            .Where(e => !e.Removed && e.Replacement is null && e.Children is null && e.Length > 4)
            .OrderBy(e => e.Offset).ToArray();
        int[] maximumEnd = new int[retained.Length];
        for (int i = 0; i < retained.Length; i++) maximumEnd[i] = Math.Max(i == 0 ? 0 : maximumEnd[i - 1], retained[i].Offset + retained[i].Length);
        foreach (Directory directory in _directories)
        {
            foreach (Entry entry in directory.Entries)
            {
                if (entry.Length <= 4 || !directory.Removed && !entry.Removed && entry.Replacement is null && entry.Children is null) continue;
                int low = 0, high = retained.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) / 2);
                    if (retained[middle].Offset < (long)entry.Offset + entry.Length) low = middle + 1; else high = middle;
                }
                if (low > 0 && maximumEnd[low - 1] > entry.Offset) throw new NotSupportedException("Cannot remove a value shared with retained EXIF data.");
                result.AsSpan(entry.Offset, entry.Length).Clear();
            }
            if (directory.Length > 0) result.AsSpan(directory.Offset, directory.Length).Clear();
        }
        int cursor = (int)valuesStart;
        for (int i = 0; i < _directories.Count; i++)
        {
            Directory directory = _directories[i];
            if (directory.Removed) continue;
            Entry[] entries = directory.Entries.Where(e => !e.Removed).OrderBy(e => e.Tag).ToArray();
            int position = offsets[i];
            Write16(result.AsSpan(position), (ushort)entries.Length);
            position += 2;
            foreach (Entry entry in entries)
            {
                Span<byte> destination = result.AsSpan(position, 12);
                Write16(destination, entry.Tag);
                Write16(destination[2..], entry.Type);
                Write32(destination[4..], entry.Count);
                byte[]? pointers = null;
                if (entry.Children is { } children)
                {
                    pointers = new byte[children.Length * 4];
                    for (int k = 0; k < children.Length; k++) Write32(pointers.AsSpan(k * 4), (uint)offsets[children[k]]);
                }
                ReadOnlySpan<byte> value = pointers is not null ? pointers : Value(entry);
                if (value.Length <= 4) value.CopyTo(destination[8..]);
                else if (entry.Replacement is null && pointers is null) Write32(destination[8..], (uint)entry.Offset);
                else
                {
                    cursor = (cursor + 1) & ~1;
                    Write32(destination[8..], (uint)cursor);
                    value.CopyTo(result.AsSpan(cursor));
                    cursor += value.Length;
                }
                position += 12;
            }
            Write32(result.AsSpan(position), directory.Next < 0 ? 0 : (uint)offsets[directory.Next]);
        }
        return result;
    }
}
