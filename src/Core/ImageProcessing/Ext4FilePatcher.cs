using System.Buffers.Binary;
using System.Security.Cryptography;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

/// <summary>Result of an in-place file replacement inside an ext4 image copy.</summary>
public sealed class Ext4PatchResult
{
    public required string ImagePath { get; init; }
    public required string FilePath { get; init; }
    public required uint Inode { get; init; }
    public required ulong OldSize { get; init; }
    public required ulong NewSize { get; init; }
    public required IReadOnlyList<ulong> DataBlocks { get; init; }
    public required uint BlockSize { get; init; }
    public required long InodeOffset { get; init; }
    public required string OldSha256 { get; init; }
    public required string NewSha256 { get; init; }
    /// <summary>Filesystem blocks whose bytes differ from the source image (verified by comparing both images).</summary>
    public IReadOnlyList<long> ChangedBlocks { get; set; } = Array.Empty<long>();
}

/// <summary>
/// Replaces the contents of one existing regular file inside a *copy* of an ext4 partition image,
/// without allocating or freeing anything: the new contents are written into the file's existing data
/// blocks and only i_size is updated. The inode number, mode, owner, timestamps and extended attributes
/// (including the SELinux label security.selinux) stay exactly as they were.
///
/// It refuses whenever this would not be a complete and consistent edit: metadata_csum (inode/extent
/// checksums), a journal that needs recovery, an unclean filesystem, inline data, uninitialized extents,
/// holes, or a new size that needs a different number of blocks than the file currently owns.
/// Any of those cases needs a real ext4 tool (e.g. debugfs/e2fsprogs in WSL).
/// </summary>
public static class Ext4FilePatcher
{
    private const uint RoCompatMetadataCsum = 0x400;
    private const uint IncompatRecover = 0x4;

    /// <summary>Checks whether <paramref name="newContent"/> can replace <paramref name="filePath"/> in place. Returns null when possible, otherwise the reason.</summary>
    public static string? CanPatch(Stream image, string filePath, long newLength)
    {
        Ext4Image fs;
        try { fs = new Ext4Image(image); } catch (Exception ex) { return $"not an ext4 image: {ex.Message}"; }
        if ((fs.FeatureRoCompat & RoCompatMetadataCsum) != 0) return "filesystem uses metadata_csum (inode checksums would have to be recomputed)";
        if ((fs.FeatureIncompat & IncompatRecover) != 0) return "filesystem journal needs recovery";
        if ((fs.State & 1) == 0 || (fs.State & 2) != 0) return $"filesystem is not marked clean (s_state={fs.State})";
        if (!fs.CanReadFiles) return "unsupported filesystem layout (meta_bg)";
        Ext4Image.FileLayout lay;
        try { lay = fs.GetFileLayout(filePath); } catch (Exception ex) { return ex.Message; }
        if (lay.InlineData) return "the file is stored as inline data";
        if (lay.HasUninitializedExtents) return "the file has uninitialized extents";
        long need = (newLength + fs.BlockSize - 1) / fs.BlockSize;
        long have = (long)((lay.Size + fs.BlockSize - 1) / fs.BlockSize);
        if (lay.Blocks.Count != have) return $"the file owns {lay.Blocks.Count} blocks but its size implies {have}";
        if (need != have) return $"the new contents need {need} block(s) but the file owns {have}; in-place patching only works when the block count stays the same";
        if (newLength == 0) return "empty replacement";
        return null;
    }

    /// <summary>
    /// Copies <paramref name="sourceImage"/> to <paramref name="outputImage"/> (new file, never overwrites),
    /// then replaces <paramref name="filePath"/> in the copy and verifies the result.
    /// </summary>
    public static Ext4PatchResult PatchCopy(string sourceImage, string outputImage, string filePath, byte[] newContent,
                                            OperationLog? log = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        using (var src = SafeFile.OpenReadOnly(sourceImage))
        {
            var why = CanPatch(src, filePath, newContent.Length);
            if (why != null) throw new ToolkitException($"Cannot patch {filePath} in place: {why}.");
            using var dst = SafeFile.CreateNew(outputImage);
            StreamCopy.CopyWithHash(src, dst, src.Length, $"Copying {Path.GetFileName(sourceImage)}", progress, ct);
        }

        Ext4PatchResult result;
        using (var img = new FileStream(outputImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var fs = new Ext4Image(img);
            var lay = fs.GetFileLayout(filePath);
            var old = fs.ReadFile(filePath, ulong.MaxValue) ?? throw new ToolkitException($"{filePath} could not be read.");
            uint bs = fs.BlockSize;

            // 1. data: new contents + zero padding over exactly the blocks the file already owns
            var buf = new byte[lay.Blocks.Count * (long)bs];
            newContent.CopyTo(buf, 0);
            for (int i = 0; i < lay.Blocks.Count; i++)
            {
                img.Position = (long)lay.Blocks[i] * bs;
                img.Write(buf, i * (int)bs, (int)bs);
            }
            // 2. inode: i_size_lo (0x04) and i_size_high (0x6C) only
            long ioff = fs.InodeOffset(lay.Inode);
            var sz = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)newContent.Length);
            img.Position = ioff + 0x04; img.Write(sz);
            BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)((ulong)newContent.Length >> 32));
            img.Position = ioff + 0x6C; img.Write(sz);
            img.Flush(true);

            result = new Ext4PatchResult
            {
                ImagePath = outputImage, FilePath = filePath, Inode = lay.Inode,
                OldSize = lay.Size, NewSize = (ulong)newContent.Length, DataBlocks = lay.Blocks, BlockSize = bs, InodeOffset = ioff,
                OldSha256 = Hex(SHA256.HashData(old)), NewSha256 = Hex(SHA256.HashData(newContent)),
            };
        }

        Verify(sourceImage, result, newContent, ct);
        log?.Info($"Patched {filePath} in {Path.GetFileName(outputImage)}: inode {result.Inode}, {result.OldSize} → {result.NewSize} bytes, blocks {string.Join(",", result.DataBlocks)}; sha256 {result.OldSha256} → {result.NewSha256}");
        log?.Info($"  changed filesystem blocks: {string.Join(", ", result.ChangedBlocks)} (inode table block + the file's data blocks only)");
        return result;
    }

    private static void Verify(string sourceImage, Ext4PatchResult r, byte[] newContent, CancellationToken ct)
    {
        // Read back through the filesystem
        using (var f = SafeFile.OpenReadOnly(r.ImagePath))
        {
            var fs = new Ext4Image(f);
            var back = fs.ReadFile(r.FilePath, ulong.MaxValue);
            if (back == null || !back.AsSpan().SequenceEqual(newContent))
                throw new ToolkitException("Verification failed: the patched file does not read back correctly.");
            var lay = fs.GetFileLayout(r.FilePath);
            if (lay.Inode != r.Inode || !lay.Blocks.SequenceEqual(r.DataBlocks))
                throw new ToolkitException("Verification failed: inode or block mapping changed.");
        }
        // Compare both images: only the inode-table block and the file's data blocks may differ.
        int bs = (int)r.BlockSize;
        var allowed = new HashSet<long>(r.DataBlocks.Select(b => (long)b)) { r.InodeOffset / bs };
        var changed = new List<long>();
        using var a = SafeFile.OpenReadOnly(sourceImage);
        using var b = SafeFile.OpenReadOnly(r.ImagePath);
        if (a.Length != b.Length) throw new ToolkitException("Verification failed: image size changed.");
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        long pos = 0;
        while (pos < a.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(ba.Length, a.Length - pos);
            a.ReadExactly(ba, 0, n); b.ReadExactly(bb, 0, n);
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n)))
                for (int off = 0; off < n; off += bs)
                    if (!ba.AsSpan(off, Math.Min(bs, n - off)).SequenceEqual(bb.AsSpan(off, Math.Min(bs, n - off))))
                        changed.Add((pos + off) / bs);
            pos += n;
        }
        var unexpected = changed.Where(c => !allowed.Contains(c)).ToList();
        if (unexpected.Count > 0) throw new ToolkitException($"Verification failed: unexpected blocks changed: {string.Join(", ", unexpected)}.");
        r.ChangedBlocks = changed;
    }

    private static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();
}
