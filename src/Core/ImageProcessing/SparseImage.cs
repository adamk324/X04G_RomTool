using System.Buffers.Binary;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

/// <summary>Android sparse image format constants (libsparse, sparse_format.h).</summary>
public static class SparseFormat
{
    public const uint Magic = 0xED26FF3A;
    public const ushort MajorVersion = 1;
    public const ushort FileHeaderSize = 28;
    public const ushort ChunkHeaderSize = 12;
    public const ushort ChunkRaw = 0xCAC1;
    public const ushort ChunkFill = 0xCAC2;
    public const ushort ChunkDontCare = 0xCAC3;
    public const ushort ChunkCrc32 = 0xCAC4;

    public static bool IsSparse(Stream s)
    {
        if (!s.CanSeek || s.Length < FileHeaderSize) return false;
        var pos = s.Position;
        try
        {
            s.Position = 0;
            Span<byte> b = stackalloc byte[4];
            if (s.ReadAtLeast(b, 4, false) < 4) return false;
            return BinaryPrimitives.ReadUInt32LittleEndian(b) == Magic;
        }
        finally { s.Position = pos; }
    }
}

/// <summary>
/// Read-only, seekable view of the *expanded* contents of an Android sparse image,
/// without writing the expanded image to disk (replaces simg2img).
/// DONT_CARE regions read as zeros.
/// </summary>
public sealed class SparseReadStream : Stream
{
    private enum Kind { Raw, Fill, DontCare }

    private readonly struct Chunk
    {
        public readonly long OutOffset;   // byte offset in expanded image
        public readonly long Length;      // bytes in expanded image
        public readonly Kind Kind;
        public readonly long DataOffset;  // RAW: offset of data in sparse file
        public readonly uint Fill;
        public Chunk(long o, long l, Kind k, long d, uint f) { OutOffset = o; Length = l; Kind = k; DataOffset = d; Fill = f; }
    }

    private readonly Stream _base;
    private readonly bool _ownsBase;
    private readonly Chunk[] _chunks;
    private long _position;

    public uint BlockSize { get; }
    public uint TotalBlocks { get; }
    public int ChunkCount => _chunks.Length;
    public long RawDataBytes { get; }

    public SparseReadStream(Stream sparse, bool ownsBase = true)
    {
        _base = sparse;
        _ownsBase = ownsBase;
        Span<byte> hdr = stackalloc byte[SparseFormat.FileHeaderSize];
        sparse.Position = 0;
        if (sparse.ReadAtLeast(hdr, hdr.Length, false) < hdr.Length) throw new InvalidImageException("Sparse image header is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != SparseFormat.Magic) throw new InvalidImageException("Not an Android sparse image (bad magic).");
        var major = BinaryPrimitives.ReadUInt16LittleEndian(hdr[4..]);
        var fileHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr[8..]);
        var chunkHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr[10..]);
        BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
        TotalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
        var totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
        if (major != SparseFormat.MajorVersion) throw new InvalidImageException($"Unsupported sparse image major version {major}.");
        if (fileHdrSz < SparseFormat.FileHeaderSize || chunkHdrSz < SparseFormat.ChunkHeaderSize)
            throw new InvalidImageException("Sparse image has invalid header sizes.");
        if (BlockSize == 0 || BlockSize % 4 != 0) throw new InvalidImageException($"Sparse image has invalid block size {BlockSize}.");

        var chunks = new List<Chunk>((int)Math.Min(totalChunks, 1_000_000));
        long filePos = fileHdrSz;
        long outBlocks = 0;
        Span<byte> ch = stackalloc byte[SparseFormat.ChunkHeaderSize];
        Span<byte> four = stackalloc byte[4];
        for (uint i = 0; i < totalChunks; i++)
        {
            sparse.Position = filePos;
            if (sparse.ReadAtLeast(ch, ch.Length, false) < ch.Length)
                throw new InvalidImageException($"Sparse image is truncated (chunk {i} of {totalChunks}).");
            var type = BinaryPrimitives.ReadUInt16LittleEndian(ch);
            var chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            var totalSz = BinaryPrimitives.ReadUInt32LittleEndian(ch[8..]);
            long dataPos = filePos + chunkHdrSz;
            long outLen = (long)chunkBlocks * BlockSize;
            long outOff = outBlocks * BlockSize;
            switch (type)
            {
                case SparseFormat.ChunkRaw:
                    if (totalSz != chunkHdrSz + outLen) throw new InvalidImageException($"Sparse RAW chunk {i} has inconsistent size.");
                    if (dataPos + outLen > sparse.Length) throw new InvalidImageException($"Sparse image is truncated inside RAW chunk {i}.");
                    chunks.Add(new Chunk(outOff, outLen, Kind.Raw, dataPos, 0));
                    RawDataBytes += outLen;
                    outBlocks += chunkBlocks;
                    break;
                case SparseFormat.ChunkFill:
                    if (totalSz != chunkHdrSz + 4) throw new InvalidImageException($"Sparse FILL chunk {i} has inconsistent size.");
                    sparse.Position = dataPos;
                    if (sparse.ReadAtLeast(four, 4, false) < 4) throw new InvalidImageException("Sparse image truncated in FILL chunk.");
                    chunks.Add(new Chunk(outOff, outLen, Kind.Fill, 0, BinaryPrimitives.ReadUInt32LittleEndian(four)));
                    outBlocks += chunkBlocks;
                    break;
                case SparseFormat.ChunkDontCare:
                    if (totalSz != chunkHdrSz) throw new InvalidImageException($"Sparse DONT_CARE chunk {i} has inconsistent size.");
                    chunks.Add(new Chunk(outOff, outLen, Kind.DontCare, 0, 0));
                    outBlocks += chunkBlocks;
                    break;
                case SparseFormat.ChunkCrc32:
                    if (totalSz != chunkHdrSz + 4) throw new InvalidImageException($"Sparse CRC chunk {i} has inconsistent size.");
                    break;
                default:
                    throw new InvalidImageException($"Sparse image has unknown chunk type 0x{type:X4} (chunk {i}).");
            }
            filePos += totalSz;
        }
        if (outBlocks != TotalBlocks)
            throw new InvalidImageException($"Sparse image chunks cover {outBlocks} blocks but the header declares {TotalBlocks}.");
        _chunks = chunks.ToArray();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => (long)TotalBlocks * BlockSize;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0) return 0;
        int total = 0;
        int idx = FindChunk(_position);
        Span<byte> pat = stackalloc byte[4];
        while (buffer.Length > 0 && _position < Length && idx < _chunks.Length)
        {
            ref readonly var c = ref _chunks[idx];
            long within = _position - c.OutOffset;
            int n = (int)Math.Min(buffer.Length, c.Length - within);
            var dst = buffer[..n];
            switch (c.Kind)
            {
                case Kind.Raw:
                    _base.Position = c.DataOffset + within;
                    _base.ReadExactly(dst);
                    break;
                case Kind.DontCare:
                    dst.Clear();
                    break;
                case Kind.Fill:
                    BinaryPrimitives.WriteUInt32LittleEndian(pat, c.Fill);
                    for (int i = 0; i < n; i++) dst[i] = pat[(int)((within + i) & 3)];
                    break;
            }
            buffer = buffer[n..];
            _position += n;
            total += n;
            idx++;
        }
        return total;
    }

    private int FindChunk(long pos)
    {
        int lo = 0, hi = _chunks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var c = _chunks[mid];
            if (pos < c.OutOffset) hi = mid - 1;
            else if (pos >= c.OutOffset + c.Length) lo = mid + 1;
            else return mid;
        }
        return _chunks.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsBase) _base.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Opens an image file (raw or sparse) and exposes its expanded contents read-only.</summary>
public sealed class ImageFile : IDisposable
{
    public string Path { get; }
    public bool IsSparse { get; }
    public long FileLength { get; }
    /// <summary>Expanded (raw) length in bytes.</summary>
    public long Length => Stream.Length;
    public Stream Stream { get; }

    private ImageFile(string path, bool sparse, long fileLength, Stream s)
    {
        Path = path; IsSparse = sparse; FileLength = fileLength; Stream = s;
    }

    public static ImageFile Open(string path)
    {
        var fs = SafeFile.OpenReadOnly(path);
        try
        {
            if (fs.Length == 0) throw new InvalidImageException($"File is empty: {path}");
            if (SparseFormat.IsSparse(fs))
                return new ImageFile(path, true, fs.Length, new SparseReadStream(fs, ownsBase: true));
            return new ImageFile(path, false, fs.Length, fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public void Dispose() => Stream.Dispose();
}

/// <summary>A read-only window [offset, offset+length) of another stream.</summary>
public sealed class SubStream : Stream
{
    private readonly Stream _base;
    private readonly long _offset;
    private readonly long _length;
    private long _pos;

    public SubStream(Stream baseStream, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > baseStream.Length)
            throw new InvalidImageException($"Region {offset}+{length} lies outside the image ({baseStream.Length} bytes).");
        _base = baseStream; _offset = offset; _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _length) return 0;
        int n = (int)Math.Min(buffer.Length, _length - _pos);
        _base.Position = _offset + _pos;
        int r = _base.Read(buffer[..n]);
        _pos += r;
        return r;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Concatenation of several regions of a base stream (a logical partition made of extents).</summary>
public sealed class ConcatRegionStream : Stream
{
    private readonly Stream _base;
    private readonly (long Offset, long Length, bool Zero)[] _regions;
    private readonly long[] _starts;
    private long _pos;

    public ConcatRegionStream(Stream baseStream, IEnumerable<(long Offset, long Length, bool Zero)> regions)
    {
        _base = baseStream;
        _regions = regions.ToArray();
        _starts = new long[_regions.Length];
        long acc = 0;
        for (int i = 0; i < _regions.Length; i++)
        {
            var r = _regions[i];
            if (!r.Zero && (r.Offset < 0 || r.Offset + r.Length > baseStream.Length))
                throw new InvalidImageException($"Extent at byte {r.Offset} (+{r.Length}) lies outside the super image ({baseStream.Length} bytes). The image may be truncated.");
            _starts[i] = acc;
            acc += r.Length;
        }
        Length = acc;
    }

    public override long Length { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _pos < Length)
        {
            int i = Array.BinarySearch(_starts, _pos);
            if (i < 0) i = ~i - 1;
            var r = _regions[i];
            long within = _pos - _starts[i];
            int n = (int)Math.Min(buffer.Length, r.Length - within);
            if (r.Zero) buffer[..n].Clear();
            else
            {
                _base.Position = r.Offset + within;
                _base.ReadExactly(buffer[..n]);
            }
            buffer = buffer[n..];
            _pos += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
