using System;
using System.IO;
using System.IO.Compression;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Decompresses a configured regular gzip file without extraction or unbounded
/// buffering. Compressed and expanded work are bounded independently. A final
/// trailer check detects truncated streams that the runtime may otherwise accept.
/// </summary>
internal sealed class HistoryCompressedInput : IDisposable
{
    private readonly BoundedInput _input;
    private readonly GZipStream _gzip;
    private readonly uint _expectedCrc, _expectedSize;
    private uint _crc = uint.MaxValue;
    private long _expanded;
    private bool _ended;
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal HistoryCompressedInput(FileStream file, long boundary)
    {
        if (boundary < 18) throw new InvalidDataException("gzip-invalid");
        Span<byte> header = stackalloc byte[3];
        Span<byte> footer = stackalloc byte[8];
        if (RandomAccess.Read(file.SafeFileHandle, header, 0) != 3 || header[0] != 0x1f || header[1] != 0x8b || header[2] != 8 || RandomAccess.Read(file.SafeFileHandle, footer, boundary - 8) != 8)
            throw new InvalidDataException("gzip-invalid");
        _expectedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer);
        _expectedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        _input = new BoundedInput(file, boundary);
        _gzip = new GZipStream(_input, CompressionMode.Decompress, leaveOpen: true);
    }

    internal void BeginPage() => _input.Budget = 8 * 1024 * 1024;
    internal bool IntegrityVerified => _ended && _expectedSize == unchecked((uint)_expanded) && _expectedCrc == ~_crc;

    internal int Read(byte[] buffer)
    {
        int count = _gzip.Read(buffer, 0, buffer.Length);
        for (int i = 0; i < count; i++) _crc = CrcTable[(_crc ^ buffer[i]) & 255] ^ (_crc >> 8);
        _expanded += count;
        if (count == 0) _ended = true;
        return count;
    }

    public void Dispose() => _gzip.Dispose();

    private static uint[] BuildCrcTable()
    {
        var values = new uint[256];
        for (uint i = 0; i < values.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            values[i] = value;
        }
        return values;
    }

    private sealed class BoundedInput(FileStream file, long boundary) : Stream
    {
        internal int Budget = 8 * 1024 * 1024;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, boundary - file.Position);
            if (count == 0) return 0;
            if (Budget <= 0) throw new InvalidDataException("gzip-input-resource-limit");
            int read = file.Read(buffer[..Math.Min(count, Budget)]); Budget -= read;
            if (read == 0) throw new InvalidDataException("gzip-truncated");
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => boundary;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
