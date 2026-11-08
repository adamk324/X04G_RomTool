using System.Buffers.Binary;

namespace X04G.Core.ImageProcessing;

/// <summary>
/// Destination for a block-device image. Blocks must be written in strictly increasing order.
/// Blocks that are never written are "don't care" (sparse) or zero (raw).
/// </summary>
public interface IDeviceImageWriter : IDisposable
{
    uint BlockSize { get; }
    ulong TotalBlocks { get; }
    void WriteBlocks(ulong firstBlock, ReadOnlySpan<byte> data);
    void FillBlocks(ulong firstBlock, ulong blockCount, uint fillValue);
    /// <summary>Completes the image. Returns the final file size in bytes.</summary>
    long Finish();
}

/// <summary>Writes an Android sparse image (v1.0), the same container format lpmake --sparse emits.</summary>
public sealed class SparseImageWriter : IDeviceImageWriter
{
    private const int MaxRawChunkBlocks = 16384; // 64 MiB per RAW chunk at 4 KiB blocks
    private readonly Stream _out;
    private ulong _nextBlock;
    private uint _chunks;
    private bool _finished;

    // Pending RAW chunk (coalesced consecutive writes)
    private long _rawHeaderPos = -1;
    private uint _rawBlocks;

    public uint BlockSize { get; }
    public ulong TotalBlocks { get; }

    public SparseImageWriter(Stream output, uint blockSize, ulong totalBlocks)
    {
        if (totalBlocks > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(totalBlocks), "Sparse images are limited to 2^32 blocks.");
        _out = output; BlockSize = blockSize; TotalBlocks = totalBlocks;
        _out.Position = 0;
        _out.Write(new byte[SparseFormat.FileHeaderSize]); // placeholder, rewritten in Finish()
    }

    private void Gap(ulong toBlock)
    {
        if (toBlock < _nextBlock) throw new InvalidOperationException($"Blocks must be written in increasing order (block {toBlock} after {_nextBlock}).");
        if (toBlock > _nextBlock)
        {
            CloseRaw();
            ulong gap = toBlock - _nextBlock;
            while (gap > 0)
            {
                uint n = (uint)Math.Min(gap, uint.MaxValue / 2);
                WriteChunkHeader(SparseFormat.ChunkDontCare, n, SparseFormat.ChunkHeaderSize);
                gap -= n;
            }
            _nextBlock = toBlock;
        }
    }

    private void WriteChunkHeader(ushort type, uint blocks, uint totalSize)
    {
        Span<byte> h = stackalloc byte[SparseFormat.ChunkHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(h, type);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], totalSize);
        _out.Write(h);
        _chunks++;
    }

    private void CloseRaw()
    {
        if (_rawHeaderPos < 0) return;
        long end = _out.Position;
        _out.Position = _rawHeaderPos + 4;
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, _rawBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], SparseFormat.ChunkHeaderSize + _rawBlocks * BlockSize);
        _out.Write(b);
        _out.Position = end;
        _rawHeaderPos = -1;
        _rawBlocks = 0;
    }

    public void WriteBlocks(ulong firstBlock, ReadOnlySpan<byte> data)
    {
        if (data.Length % BlockSize != 0) throw new ArgumentException("Data must be a whole number of blocks.");
        ulong blocks = (ulong)data.Length / BlockSize;
        if (firstBlock + blocks > TotalBlocks) throw new InvalidOperationException("Write past the end of the device image.");
        Gap(firstBlock);
        while (data.Length > 0)
        {
            if (_rawHeaderPos < 0 || _rawBlocks >= MaxRawChunkBlocks)
            {
                CloseRaw();
                _rawHeaderPos = _out.Position;
                WriteChunkHeader(SparseFormat.ChunkRaw, 0, 0);
            }
            int canBlocks = (int)Math.Min((ulong)(MaxRawChunkBlocks - _rawBlocks), (ulong)data.Length / BlockSize);
            int bytes = canBlocks * (int)BlockSize;
            _out.Write(data[..bytes]);
            _rawBlocks += (uint)canBlocks;
            _nextBlock += (ulong)canBlocks;
            data = data[bytes..];
        }
    }

    public void FillBlocks(ulong firstBlock, ulong blockCount, uint fillValue)
    {
        if (blockCount == 0) return;
        if (firstBlock + blockCount > TotalBlocks) throw new InvalidOperationException("Fill past the end of the device image.");
        Gap(firstBlock);
        CloseRaw();
        Span<byte> v = stackalloc byte[4];
        while (blockCount > 0)
        {
            uint n = (uint)Math.Min(blockCount, uint.MaxValue / 2);
            WriteChunkHeader(SparseFormat.ChunkFill, n, SparseFormat.ChunkHeaderSize + 4u);
            BinaryPrimitives.WriteUInt32LittleEndian(v, fillValue);
            _out.Write(v);
            blockCount -= n;
            _nextBlock += n;
        }
    }

    public long Finish()
    {
        if (_finished) return _out.Length;
        Gap(TotalBlocks);
        CloseRaw();
        long end = _out.Position;
        _out.Position = 0;
        Span<byte> h = stackalloc byte[SparseFormat.FileHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, SparseFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], SparseFormat.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], SparseFormat.FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], SparseFormat.ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], _chunks);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], 0);
        _out.Write(h);
        _out.Position = end;
        _out.SetLength(end);
        _out.Flush();
        _finished = true;
        return end;
    }

    public void Dispose() => _out.Dispose();
}

/// <summary>Writes a plain raw image of exactly TotalBlocks*BlockSize bytes (like a super.bin dump).</summary>
public sealed class RawImageWriter : IDeviceImageWriter
{
    private readonly Stream _out;
    private ulong _nextBlock;
    public uint BlockSize { get; }
    public ulong TotalBlocks { get; }

    public RawImageWriter(Stream output, uint blockSize, ulong totalBlocks)
    {
        _out = output; BlockSize = blockSize; TotalBlocks = totalBlocks;
        // Extending a file reads back as zeros on NTFS/ext4, so unwritten regions are zero.
        _out.SetLength((long)(totalBlocks * blockSize));
    }

    private void Check(ulong first, ulong count)
    {
        if (first < _nextBlock) throw new InvalidOperationException("Blocks must be written in increasing order.");
        if (first + count > TotalBlocks) throw new InvalidOperationException("Write past the end of the device image.");
        _nextBlock = first + count;
    }

    public void WriteBlocks(ulong firstBlock, ReadOnlySpan<byte> data)
    {
        if (data.Length % BlockSize != 0) throw new ArgumentException("Data must be a whole number of blocks.");
        Check(firstBlock, (ulong)data.Length / BlockSize);
        _out.Position = (long)(firstBlock * BlockSize);
        _out.Write(data);
    }

    public void FillBlocks(ulong firstBlock, ulong blockCount, uint fillValue)
    {
        Check(firstBlock, blockCount);
        if (fillValue == 0) return; // already zero
        var buf = new byte[BlockSize * 256];
        for (int i = 0; i < buf.Length; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i), fillValue);
        _out.Position = (long)(firstBlock * BlockSize);
        while (blockCount > 0)
        {
            int n = (int)Math.Min(blockCount, 256UL);
            _out.Write(buf, 0, n * (int)BlockSize);
            blockCount -= (ulong)n;
        }
    }

    public long Finish()
    {
        _out.Flush();
        return _out.Length;
    }

    public void Dispose() => _out.Dispose();
}
