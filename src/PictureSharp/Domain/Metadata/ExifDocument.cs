using System.Buffers.Binary;

namespace PictureSharp.Domain.Metadata;

/// <summary>Validated classic TIFF directories. Unchanged opaque values retain their original absolute offsets.</summary>
internal sealed partial class ExifDocument
{
    internal sealed class Entry(ushort tag, ushort type, uint count, int offset, int length)
    {
        public ushort Tag = tag;
        public ushort Type = type;
        public uint Count = count;
        public int Offset = offset;
        public int Length = length;
        public byte[]? Replacement;
        public int[]? Children;
        public bool Removed;
    }

    private sealed class Directory(int offset, int length, int kind)
    {
        public int Offset = offset;
        public int Length = length;
        public int Kind = kind;
        public List<Entry> Entries = [];
        public int Next = -1;
        public bool Removed;
    }

    private readonly byte[] _source;
    private readonly bool _big;
    private long _parsedBytes;
    private CancellationToken _token;
    private readonly List<Directory> _directories = [];
    private ExifDocument(byte[] source, bool big) { _source = source; _big = big; }

    public static ExifDocument Parse(byte[]? source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source is null || source.Length == 0) return new([], false);
        if (source.Length < 8) throw Error("Truncated TIFF header.");
        bool big = source.AsSpan().StartsWith("MM"u8);
        if (!big && !source.AsSpan().StartsWith("II"u8)) throw Error("Invalid TIFF byte order.");
        var document = new ExifDocument(source, big) { _token = token };
        if (document.Read16(source.AsSpan(2)) != 42) throw Error("Invalid classic TIFF header.");
        uint first = document.Read32(source.AsSpan(4));
        if (first == 0) return document;
        document.ReadDirectory(first, 0, 0, new HashSet<uint>());
        var ranges = document._directories.OrderBy(d => d.Offset).ToArray();
        int end = 8;
        foreach (Directory directory in ranges)
        {
            if (directory.Offset < end) throw Error("Overlapping TIFF directories.");
            end = directory.Offset + directory.Length;
        }
        foreach (Entry entry in document._directories.SelectMany(d => d.Entries).Where(e => e.Length > 4))
        {
            int low = 0, high = ranges.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (ranges[middle].Offset + ranges[middle].Length <= entry.Offset) low = middle + 1;
                else high = middle;
            }
            if (entry.Offset < 8 || low < ranges.Length && ranges[low].Offset < (long)entry.Offset + entry.Length)
                throw Error("TIFF value overlaps a directory or header.");
        }
        return document;
    }

    private int ReadDirectory(uint offset, int kind, int depth, HashSet<uint> visited)
    {
        _token.ThrowIfCancellationRequested();
        if (depth > 64) throw new NotSupportedException("EXIF directory nesting exceeds 64 levels.");
        if (!visited.Add(offset)) throw Error("Cyclic or shared TIFF directories.");
        if (offset < 8 || offset > _source.Length - 2) throw Error("Invalid TIFF directory offset.");
        int count = Read16(_source.AsSpan((int)offset));
        long size = 2L + (count * 12L) + 4;
        if (size > _source.Length - offset) throw Error("Truncated TIFF directory.");
        // Every visited directory must occupy distinct bytes; cap work before validating overlap.
        if (_directories.Count > _source.Length / 6) throw Error("Too many TIFF directories.");
        _parsedBytes += size;
        if (_parsedBytes > _source.Length) throw Error("Overlapping TIFF directories.");
        var directory = new Directory((int)offset, (int)size, kind);
        int index = _directories.Count;
        _directories.Add(directory);
        var tags = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            if ((i & 255) == 0) _token.ThrowIfCancellationRequested();
            int position = (int)offset + 2 + (12 * i);
            ReadOnlySpan<byte> bytes = _source.AsSpan(position, 12);
            ushort tag = Read16(bytes);
            if (!tags.Add(tag)) throw Error("Duplicate TIFF tag.");
            ushort type = Read16(bytes[2..]);
            uint items = Read32(bytes[4..]);
            int unit = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8,
                16 or 17 or 18 => throw new NotSupportedException($"EXIF TIFF type {type} is not supported for editing."),
                _ => throw Error($"Invalid TIFF type {type}.") };
            long length = (long)items * unit;
            long start = length <= 4 ? position + 8 : Read32(bytes[8..]);
            if (length > int.MaxValue || start > _source.Length || length > _source.Length - start) throw Error("Invalid TIFF value offset or length.");
            var entry = new Entry(tag, type, items, (int)start, (int)length);
            directory.Entries.Add(entry);
        }
        foreach (Entry entry in directory.Entries)
        {
            if (entry.Tag is not (0x8769 or 0x8825 or 0xa005 or 0x014a)) continue;
            if (entry.Type is not (4 or 13) || entry.Count == 0 || entry.Tag != 0x014a && entry.Count != 1)
                throw Error("Invalid TIFF directory pointer.");
            entry.Children = new int[entry.Count];
            for (int i = 0; i < entry.Children.Length; i++)
            {
                uint child = Read32(Value(entry)[(i * 4)..]);
                int childKind = kind == 2 || entry.Tag == 0x8825 ? 2 : entry.Tag == 0x8769 ? 1 : 3;
                entry.Children[i] = ReadDirectory(child, childKind, depth + 1, visited);
            }
        }
        uint next = Read32(_source.AsSpan((int)offset + (int)size - 4));
        if (next != 0) directory.Next = ReadDirectory(next, kind == 2 ? 2 : 3, depth + 1, visited);
        return index;
    }

    public Entry? Find(ExifTag tag) => _directories.Where(d => !d.Removed && d.Kind == tag.Directory)
        .SelectMany(d => d.Entries).FirstOrDefault(e => !e.Removed && e.Tag == tag.Id);
    public ReadOnlySpan<byte> Value(Entry entry) => entry.Replacement is { } bytes ? bytes : _source.AsSpan(entry.Offset, entry.Length);
    public ushort Read16(ReadOnlySpan<byte> bytes) => _big ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    public uint Read32(ReadOnlySpan<byte> bytes) => _big ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    public void Write16(Span<byte> bytes, ushort value)
    {
        if (_big) BinaryPrimitives.WriteUInt16BigEndian(bytes, value); else BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    }
    public void Write32(Span<byte> bytes, uint value)
    {
        if (_big) BinaryPrimitives.WriteUInt32BigEndian(bytes, value); else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }

    public void Set(ExifTag tag, byte[] value, uint count)
    {
        if (_directories.Count == 0) _directories.Add(new(0, 0, 0));
        Directory? directory = _directories.FirstOrDefault(d => !d.Removed && d.Kind == tag.Directory);
        if (directory is null)
        {
            directory = new(0, 0, tag.Directory);
            if (tag.Directory == 2) directory.Entries.Add(new(0, 1, 4, 0, 0) { Replacement = [2, 3, 0, 0] });
            int child = _directories.Count;
            _directories.Add(directory);
            _directories[0].Entries.Add(new((ushort)(tag.Directory == 1 ? 0x8769 : 0x8825), 4, 1, 0, 0) { Children = [child] });
        }
        Entry? entry = directory.Entries.FirstOrDefault(e => !e.Removed && e.Tag == tag.Id);
        if (entry is null) { entry = new(tag.Id, tag.Type, count, 0, 0); directory.Entries.Add(entry); }
        entry.Type = tag.Type;
        entry.Count = count;
        entry.Replacement = value;
    }

    public bool Remove(ExifTag tag)
    {
        Entry? entry = Find(tag);
        if (entry is null) return false;
        entry.Removed = true;
        return true;
    }

    public bool RemoveGps()
    {
        bool removed = false;
        foreach (Directory directory in _directories)
        {
            if (directory.Kind == 2) { directory.Removed = true; removed = true; }
            foreach (Entry entry in directory.Entries.Where(e => e.Tag == 0x8825 && e.Children is not null)) entry.Removed = true;
        }
        return removed;
    }

    public byte[]? KeepOnly(ReadOnlySpan<ExifTag> tags)
    {
        var selected = new ExifDocument([], _big);
        foreach (ExifTag tag in tags) // Repeated tags overwrite the same entry.
            if (Find(tag) is { } entry)
            {
                if (entry.Type != tag.Type) throw new NotSupportedException("Cannot retain a tag with an unsupported EXIF type.");
                selected.Set(tag, Value(entry).ToArray(), entry.Count);
            }
        return selected.Serialize();
    }

    private static ImageFormatException Error(string message) => new($"Malformed EXIF: {message}");
}
