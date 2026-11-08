using System.Buffers.Binary;
using System.Security.Cryptography;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

/// <summary>
/// A super partition image (raw super.bin dump or sparse/raw super.img), opened read-only.
/// Replaces simg2img + lpunpack: partitions are read directly through their extents.
/// </summary>
public sealed class SuperImage : IDisposable
{
    public ImageFile File { get; }
    public LpMetadata Metadata { get; }
    public IReadOnlyList<string> Warnings { get; }

    private SuperImage(ImageFile f, LpReadResult r)
    {
        File = f;
        Metadata = r.Metadata;
        Warnings = r.Warnings;
    }

    public static SuperImage Open(string path)
    {
        var f = ImageFile.Open(path);
        try
        {
            if (!LpMetadataReader.LooksLikeSuper(f.Stream))
                throw new InvalidImageException(
                    $"'{System.IO.Path.GetFileName(path)}' is not a super (dynamic partition) image: no LP geometry at offset 4096." +
                    (f.IsSparse ? " (The file is a valid sparse image, but its contents are not a super partition.)" : ""));
            var r = LpMetadataReader.Read(f.Stream);
            return new SuperImage(f, r);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    /// <summary>Read-only stream of a logical partition's contents (concatenated extents).</summary>
    public Stream OpenPartition(string name)
    {
        var p = Metadata.FindPartition(name) ?? throw new ToolkitException($"Partition '{name}' does not exist in this super image.");
        var regions = new List<(long, long, bool)>();
        foreach (var e in Metadata.ExtentsOf(p))
        {
            if (e.TargetType == LpFormat.TargetZero) regions.Add((0, (long)e.Bytes, true));
            else if (e.TargetSource != 0)
                throw new ToolkitException($"Partition '{name}' lives on block device #{e.TargetSource}, which is not part of this file (retrofit layout).");
            else regions.Add(((long)(e.TargetData * LpFormat.SectorSize), (long)e.Bytes, false));
        }
        return new ConcatRegionStream(File.Stream, regions);
    }

    /// <summary>Extracts a partition to a new file (never overwrites). Returns the SHA-256 of the written data.</summary>
    public byte[] ExtractPartition(string name, string outputPath, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        using var src = OpenPartition(name);
        using var dst = SafeFile.CreateNew(outputPath);
        return StreamCopy.CopyWithHash(src, dst, src.Length, $"Extracting {name}", progress, ct);
    }

    public void Dispose() => File.Dispose();
}

public static class StreamCopy
{
    public const int BufferSize = 4 << 20;

    public static byte[] CopyWithHash(Stream src, Stream dst, long length, string stage, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BufferSize];
        long done = 0;
        src.Position = 0;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - done);
            src.ReadExactly(buf, 0, n);
            dst.Write(buf, 0, n);
            sha.AppendData(buf, 0, n);
            done += n;
            progress?.Report(new ProgressInfo(stage, length == 0 ? 1 : (double)done / length));
        }
        dst.Flush();
        return sha.GetHashAndReset();
    }

    public static byte[] Hash(Stream src, long length, string stage, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BufferSize];
        long done = 0;
        src.Position = 0;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - done);
            src.ReadExactly(buf, 0, n);
            sha.AppendData(buf, 0, n);
            done += n;
            progress?.Report(new ProgressInfo(stage, length == 0 ? 1 : (double)done / length));
        }
        return sha.GetHashAndReset();
    }
}

/// <summary>Content for one logical partition of a new super image.</summary>
public sealed class PartitionContent
{
    public required string Name { get; init; }
    /// <summary>Opens the (expanded/raw) contents. Called once during the build.</summary>
    public required Func<Stream> Open { get; init; }
    public required long Length { get; init; }
    public string SourceDescription { get; init; } = "";
}

public sealed class SuperBuildResult
{
    public required LpMetadata Metadata { get; init; }
    public long OutputFileSize { get; set; }
    public Dictionary<string, byte[]> PartitionHashes { get; } = new();
    public Dictionary<string, long> PartitionLengths { get; } = new();
}

/// <summary>
/// Writes a super image from planned metadata and partition contents, following AOSP liblp images.cpp
/// (ImageBuilder::Build / AddPartitionImage): 4 KiB reserved zeros, 2x geometry, 2x each metadata slot,
/// then partition data block by block (uniform 32-bit patterns become FILL chunks, gaps are DONT_CARE).
/// </summary>
public static class SuperImageBuilder
{
    public static SuperBuildResult Build(LpMetadata md, IReadOnlyList<PartitionContent> contents, IDeviceImageWriter writer,
                                         IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        uint bs = md.Geometry.LogicalBlockSize;
        if (writer.BlockSize != bs) throw new ArgumentException("Writer block size must equal the logical block size.");
        if ((ulong)writer.TotalBlocks * bs != md.SuperDevice.Size) throw new ArgumentException("Writer size must equal the super device size.");
        if (LpFormat.PartitionReservedBytes % bs != 0 && bs != LpFormat.PartitionReservedBytes)
            throw new NotSupportedException("Logical block size must be 4096 (or divide 4096).");

        // Reserved area + metadata, contiguous from byte 0.
        var geometry = LpMetadataWriter.SerializeGeometry(md.Geometry);
        var metaBlob = LpMetadataWriter.SerializeMetadata(md);
        var meta = new byte[md.Geometry.MetadataMaxSize];
        metaBlob.CopyTo(meta, 0);
        long reservedLen = LpFormat.PartitionReservedBytes + 2L * LpFormat.GeometrySize + 2L * md.Geometry.MetadataSlotCount * md.Geometry.MetadataMaxSize;
        if (reservedLen % bs != 0) throw new NotSupportedException("Metadata area is not block aligned.");
        writer.FillBlocks(0, LpFormat.PartitionReservedBytes / bs, 0);
        var area = new byte[reservedLen - LpFormat.PartitionReservedBytes];
        geometry.CopyTo(area, 0);
        geometry.CopyTo(area, LpFormat.GeometrySize);
        for (int i = 0; i < md.Geometry.MetadataSlotCount * 2; i++)
            meta.CopyTo(area, 2 * LpFormat.GeometrySize + i * meta.Length);
        writer.WriteBlocks(LpFormat.PartitionReservedBytes / bs, area);

        // Extents must be increasing in the table (images.cpp CheckExtentOrdering).
        ulong last = 0;
        foreach (var e in md.Extents)
        {
            if (e.TargetType != LpFormat.TargetLinear) throw new NotSupportedException("Only linear extents can be written.");
            if (e.TargetData <= last && last != 0) throw new InvalidOperationException("Extents must appear in increasing order.");
            if (e.Bytes % bs != 0) throw new InvalidOperationException("Extents must be block aligned.");
            last = e.TargetData;
        }

        var result = new SuperBuildResult { Metadata = md };
        var byName = contents.ToDictionary(c => c.Name);
        long totalBytes = contents.Sum(c => c.Length);
        long doneBytes = 0;
        foreach (var p in md.Partitions)
        {
            if (!byName.TryGetValue(p.Name, out var content)) continue;
            ulong partSize = md.SizeOf(p);
            if ((ulong)content.Length > partSize)
                throw new ToolkitException($"Image for partition '{p.Name}' is larger than the partition ({content.Length:N0} > {partSize:N0} bytes).");

            using var src = content.Open();
            if (src.Length < content.Length) throw new ToolkitException($"Image for '{p.Name}' is shorter than expected.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[StreamCopy.BufferSize];
            long remaining = content.Length;
            src.Position = 0;
            foreach (var e in md.ExtentsOf(p))
            {
                ulong block = e.TargetData * LpFormat.SectorSize / bs;
                ulong extentBlocks = e.Bytes / bs;
                while (extentBlocks > 0 && remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min((ulong)buf.Length / bs, extentBlocks) * (int)bs;
                    int dataBytes = (int)Math.Min(want, remaining);
                    src.ReadExactly(buf, 0, dataBytes);
                    sha.AppendData(buf, 0, dataBytes);
                    int blocks = (dataBytes + (int)bs - 1) / (int)bs;
                    if (dataBytes < blocks * bs) Array.Clear(buf, dataBytes, blocks * (int)bs - dataBytes);
                    WriteRun(writer, block, buf.AsSpan(0, blocks * (int)bs), bs, dataBytes);
                    block += (ulong)blocks;
                    extentBlocks -= (ulong)blocks;
                    remaining -= dataBytes;
                    doneBytes += dataBytes;
                    progress?.Report(new ProgressInfo($"Writing {p.Name}", totalBytes == 0 ? 1 : (double)doneBytes / totalBytes));
                }
                if (remaining == 0) break;
            }
            if (remaining > 0) throw new ToolkitException($"Partition '{p.Name}' extents are too small for its image.");
            result.PartitionHashes[p.Name] = sha.GetHashAndReset();
            result.PartitionLengths[p.Name] = content.Length;
        }
        foreach (var c in contents)
            if (md.FindPartition(c.Name) == null) throw new ToolkitException($"An image was supplied for '{c.Name}', but no such partition was planned.");

        result.OutputFileSize = writer.Finish();
        return result;
    }

    /// <summary>Per block: uniform 32-bit value → FILL, otherwise RAW (coalesced). Mirrors lpmake's HasFillValue logic.
    /// A final partial block (image not a multiple of the block size) is always written raw, like lpmake.</summary>
    private static void WriteRun(IDeviceImageWriter w, ulong firstBlock, ReadOnlySpan<byte> data, uint bs, int dataBytes)
    {
        int blocks = data.Length / (int)bs;
        int i = 0;
        while (i < blocks)
        {
            bool partial = (long)(i + 1) * bs > dataBytes;
            if (!partial && IsFill(data.Slice(i * (int)bs, (int)bs), out uint v))
            {
                int j = i + 1;
                while (j < blocks && (long)(j + 1) * bs <= dataBytes && IsFill(data.Slice(j * (int)bs, (int)bs), out uint v2) && v2 == v) j++;
                w.FillBlocks(firstBlock + (ulong)i, (ulong)(j - i), v);
                i = j;
            }
            else
            {
                int j = i + 1;
                while (j < blocks && ((long)(j + 1) * bs > dataBytes || !IsFill(data.Slice(j * (int)bs, (int)bs), out _))) j++;
                w.WriteBlocks(firstBlock + (ulong)i, data.Slice(i * (int)bs, (j - i) * (int)bs));
                i = j;
            }
        }
    }

    private static bool IsFill(ReadOnlySpan<byte> block, out uint value)
    {
        value = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(block);
        uint first = words[0];
        for (int k = 1; k < words.Length; k++) if (words[k] != first) return false;
        return true;
    }
}
