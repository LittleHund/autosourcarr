using System.Buffers.Binary;
using System.Text;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>Bounded sorted runs, binary carry/two-way merge. All files remain actual owned handles.</summary>
internal sealed class ReplaySort(OwnedReplayFiles files, CancellationToken cancellationToken)
{
    private sealed record Run(FileStream File, long Count);
    private sealed class Bytes : IComparer<byte[]>
    { public static readonly Bytes Instance = new(); public int Compare(byte[]? a, byte[]? b) => (a ?? []).AsSpan().SequenceCompareTo(b ?? []); }
    private readonly List<byte[]> _pending = [];
    private readonly List<Run?> _levels = [];
    private long _bytes;
    public bool Duplicates { get; private set; }
    public void Add(byte[] value)
    {
        cancellationToken.ThrowIfCancellationRequested(); _pending.Add(value); _bytes = checked(_bytes + value.Length);
        if (_pending.Count >= 4096 || _bytes >= 1024 * 1024) Flush();
    }
    private void Flush()
    {
        if (_pending.Count == 0) return;
        _pending.Sort(Bytes.Instance); var file = files.Create(); long count = 0; byte[]? previous = null;
        foreach (var value in _pending)
        { cancellationToken.ThrowIfCancellationRequested(); if (previous is not null && previous.AsSpan().SequenceEqual(value)) { Duplicates = true; continue; } Write(file, value); previous = value; count++; }
        _pending.Clear(); _bytes = 0; var run = new Run(file, count); var level = 0;
        while (level < _levels.Count && _levels[level] is { } existing)
        { run = Merge(existing, run); _levels[level++] = null; }
        if (level == _levels.Count) _levels.Add(run); else _levels[level] = run;
    }
    private Run Merge(Run left, Run right)
    {
        cancellationToken.ThrowIfCancellationRequested(); var result = files.Create(); left.File.Position = 0; right.File.Position = 0;
        long li = 0, ri = 0, count = 0; var a = li < left.Count ? Read(left.File) : null; var b = ri < right.Count ? Read(right.File) : null;
        while (a is not null || b is not null)
        {
            cancellationToken.ThrowIfCancellationRequested(); var compare = a is null ? 1 : b is null ? -1 : Bytes.Instance.Compare(a, b);
            if (compare <= 0) { Write(result, a!); count = checked(count + 1); a = ++li < left.Count ? Read(left.File) : null; }
            if (compare == 0) { Duplicates = true; b = ++ri < right.Count ? Read(right.File) : null; }
            if (compare > 0) { Write(result, b!); count = checked(count + 1); b = ++ri < right.Count ? Read(right.File) : null; }
        }
        files.Release(left.File); files.Release(right.File); return new(result, count);
    }
    public (FileStream File, long Count) Finish()
    {
        Flush(); Run? result = null;
        foreach (var run in _levels) if (run is not null) result = result is null ? run : Merge(result, run);
        _levels.Clear(); result ??= new(files.Create(), 0); result.File.Flush(); result.File.Position = 0; return (result.File, result.Count);
    }
    private static void Write(Stream stream, byte[] value)
    { Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, value.Length); stream.Write(length); stream.Write(value); }
    private static byte[] Read(Stream stream)
    { Span<byte> length = stackalloc byte[4]; stream.ReadExactly(length); var size = BinaryPrimitives.ReadInt32LittleEndian(length); if (size < 0) throw new InvalidDataException("Owned chapter sort run is corrupt"); var value = new byte[size]; stream.ReadExactly(value); return value; }
    public static byte[] Integer(long value) { var result = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(result, unchecked((ulong)value) ^ 0x8000000000000000); return result; }
}

internal sealed class ReplayIdIndex(FileStream file, long count)
{
    public bool Contains(long value)
    {
        var key = ReplaySort.Integer(value); long low = 0, high = count - 1; Span<byte> bytes = stackalloc byte[12];
        while (low <= high)
        {
            var mid = low + (high - low) / 2; file.Position = checked(mid * 12); file.ReadExactly(bytes);
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != 8) throw new InvalidDataException("Owned replay identity index is corrupt");
            var compare = bytes[4..].SequenceCompareTo(key); if (compare == 0) return true; if (compare < 0) low = mid + 1; else high = mid - 1;
        }
        return false;
    }
    public static ReplayIdIndex Read(Stream source, OwnedReplayFiles files, CancellationToken cancellationToken)
    {
        var sort = new ReplaySort(files, cancellationToken); var reader = new IdReader(source, cancellationToken); reader.Parse(value => sort.Add(ReplaySort.Integer(value)));
        var result = sort.Finish(); return new(result.File, result.Count);
    }
    /// <summary>The accepted JSON grammar is an array of exact int64 tokens. Its entire alphabet is strict ASCII/UTF8.</summary>
    private sealed class IdReader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[8192]; private int _position, _count; private int _look = -2;
        private int Take() { var value = Peek(); _look = -2; return value; }
        private int Peek()
        {
            if (_look != -2) return _look;
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == _count) { _count = stream.Read(_buffer); _position = 0; if (_count == 0) return _look = -1; }
            return _look = _buffer[_position++];
        }
        private void Space() { while (Peek() is 9 or 10 or 13 or 32) Take(); }
        private void Expect(int value) { if (Take() != value) throw new InvalidDataException("Saved gallery candidate IDs must be a complete UTF8 JSON integer array"); }
        public void Parse(Action<long> value)
        {
            Space(); Expect('['); Space();
            if (Peek() != ']')
                while (true)
                {
                    var negative = Peek() == '-'; if (negative) Take(); var first = Take(); if (first is < '0' or > '9') throw new InvalidDataException("Saved gallery candidate ID is not an exact integer");
                    ulong integer = (ulong)(first - '0'); var limit = negative ? 0x8000000000000000UL : (ulong)long.MaxValue;
                    if (first == '0' && Peek() is >= '0' and <= '9') throw new InvalidDataException("Saved gallery candidate ID has a leading zero");
                    while (Peek() is >= '0' and <= '9') { var digit = (ulong)(Take() - '0'); if (integer > (limit - digit) / 10) throw new InvalidDataException("Saved gallery candidate ID exceeds int64"); integer = integer * 10 + digit; }
                    value(negative ? integer == 0x8000000000000000UL ? long.MinValue : -(long)integer : (long)integer); Space();
                    if (Peek() != ',') break; Take(); Space();
                }
            Expect(']'); Space(); if (Take() != -1) throw new InvalidDataException("Saved gallery candidate intent has trailing/invalid UTF8 content");
        }
    }
}
