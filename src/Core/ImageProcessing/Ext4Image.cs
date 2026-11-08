using System.Buffers.Binary;
using System.Text;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

public enum FileSystemKind { Unknown, Ext4, Erofs, F2fs, Squashfs, Empty }

public static class FileSystemDetector
{
    public static FileSystemKind Detect(Stream s)
    {
        if (s.Length < 2048) return FileSystemKind.Unknown;
        var b = new byte[2048];
        s.Position = 0;
        if (s.ReadAtLeast(b, b.Length, false) < b.Length) return FileSystemKind.Unknown;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(1024 + 0x38)) == 0xEF53) return FileSystemKind.Ext4;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1024)) == 0xE0F5E1E2) return FileSystemKind.Erofs;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1024)) == 0xF2F52010) return FileSystemKind.F2fs;
        if (b[0] == 'h' && b[1] == 's' && b[2] == 'q' && b[3] == 's') return FileSystemKind.Squashfs;
        if (b.All(x => x == 0)) return FileSystemKind.Empty;
        return FileSystemKind.Unknown;
    }

    /// <summary>Recognizes compressed/archived downloads that must be extracted before use.</summary>
    public static string? ArchiveKind(Stream s)
    {
        var b = new byte[8];
        s.Position = 0;
        if (s.ReadAtLeast(b, 8, false) < 8) return null;
        if (b[0] == 0xFD && b[1] == (byte)'7' && b[2] == (byte)'z' && b[3] == (byte)'X' && b[4] == (byte)'Z') return "xz";
        if (b[0] == 0x1F && b[1] == 0x8B) return "gzip";
        if (b[0] == (byte)'P' && b[1] == (byte)'K' && b[2] == 3 && b[3] == 4) return "zip";
        if (b[0] == (byte)'7' && b[1] == (byte)'z' && b[2] == 0xBC && b[3] == 0xAF) return "7z";
        if (b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD) return "zstd";
        if (b[0] == (byte)'B' && b[1] == (byte)'Z' && b[2] == (byte)'h') return "bzip2";
        return null;
    }
}

/// <summary>
/// Minimal read-only ext2/3/4 reader: superblock statistics, path lookup and file reads
/// (extents, classic block maps, inline data up to 60 bytes, fast symlinks). Used to read
/// build.prop from system/vendor images on Windows without mounting them.
/// </summary>
public sealed class Ext4Image
{
    // Feature flags
    public const uint IncompatFiletype = 0x2, IncompatMetaBg = 0x10, IncompatExtents = 0x40, Incompat64Bit = 0x80, IncompatInlineData = 0x8000;
    public const uint RoCompatSharedBlocks = 0x4000;
    public const uint CompatHasJournal = 0x4;

    private readonly Stream _s;
    private readonly long[] _inodeTables;

    public uint BlockSize { get; }
    public ulong BlocksCount { get; }
    public ulong FreeBlocks { get; }
    public uint InodesCount { get; }
    public uint FreeInodes { get; }
    public uint InodeSize { get; }
    public uint InodesPerGroup { get; }
    public uint BlocksPerGroup { get; }
    public uint FirstDataBlock { get; }
    public uint FeatureCompat { get; }
    public uint FeatureIncompat { get; }
    public uint FeatureRoCompat { get; }
    public string VolumeName { get; }
    public string LastMounted { get; }
    public int GroupCount { get; }

    public ulong FilesystemBytes => BlocksCount * BlockSize;
    public ulong UsedBytes => (BlocksCount - Math.Min(FreeBlocks, BlocksCount)) * BlockSize;
    public bool HasSharedBlocks => (FeatureRoCompat & RoCompatSharedBlocks) != 0;
    public bool HasJournal => (FeatureCompat & CompatHasJournal) != 0;

    public Ext4Image(Stream s)
    {
        _s = s;
        var sb = new byte[1024];
        s.Position = 1024;
        if (s.ReadAtLeast(sb, 1024, false) < 1024) throw new InvalidImageException("ext4 superblock is truncated.");
        var b = sb.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(b[0x38..]) != 0xEF53) throw new InvalidImageException("Not an ext2/3/4 filesystem (bad superblock magic).");
        InodesCount = BinaryPrimitives.ReadUInt32LittleEndian(b);
        uint blocksLo = BinaryPrimitives.ReadUInt32LittleEndian(b[0x04..]);
        uint freeLo = BinaryPrimitives.ReadUInt32LittleEndian(b[0x0C..]);
        FreeInodes = BinaryPrimitives.ReadUInt32LittleEndian(b[0x10..]);
        FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(b[0x14..]);
        uint logBs = BinaryPrimitives.ReadUInt32LittleEndian(b[0x18..]);
        if (logBs > 6) throw new InvalidImageException($"ext4 superblock has invalid block size (log {logBs}).");
        BlockSize = 1024u << (int)logBs;
        BlocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(b[0x20..]);
        InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(b[0x28..]);
        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(b[0x4C..]);
        InodeSize = rev == 0 ? 128u : BinaryPrimitives.ReadUInt16LittleEndian(b[0x58..]);
        FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(b[0x5C..]);
        FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(b[0x60..]);
        FeatureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(b[0x64..]);
        VolumeName = ReadCString(b.Slice(0x78, 16));
        LastMounted = ReadCString(b.Slice(0x88, 64));
        bool is64 = (FeatureIncompat & Incompat64Bit) != 0;
        uint descSize = is64 ? Math.Max(32u, BinaryPrimitives.ReadUInt16LittleEndian(b[0xFE..])) : 32u;
        BlocksCount = blocksLo | (is64 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b[0x150..]) << 32 : 0);
        FreeBlocks = freeLo | (is64 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b[0x158..]) << 32 : 0);
        if (BlocksPerGroup == 0 || InodesPerGroup == 0 || InodeSize < 128) throw new InvalidImageException("ext4 superblock has invalid geometry.");
        GroupCount = (int)((BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup);
        if ((FeatureIncompat & IncompatMetaBg) != 0)
        {
            _inodeTables = Array.Empty<long>(); // meta_bg layout is not supported for file reads; stats still valid.
            return;
        }
        long gdtOffset = (long)(FirstDataBlock + 1) * BlockSize;
        var gdt = new byte[(long)GroupCount * descSize];
        s.Position = gdtOffset;
        if (s.ReadAtLeast(gdt, gdt.Length, false) < gdt.Length) throw new InvalidImageException("ext4 group descriptor table is truncated.");
        _inodeTables = new long[GroupCount];
        for (int g = 0; g < GroupCount; g++)
        {
            var d = gdt.AsSpan((int)(g * descSize), (int)descSize);
            ulong it = BinaryPrimitives.ReadUInt32LittleEndian(d[0x08..]);
            if (is64 && descSize >= 64) it |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(d[0x28..]) << 32;
            _inodeTables[g] = (long)it * BlockSize;
        }
    }

    public bool CanReadFiles => _inodeTables.Length > 0;

    private static string ReadCString(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? b : b[..n]).Trim();
    }

    public IReadOnlyList<string> FeatureNames()
    {
        var list = new List<string>();
        if (HasJournal) list.Add("has_journal");
        if ((FeatureIncompat & IncompatExtents) != 0) list.Add("extent");
        if ((FeatureIncompat & Incompat64Bit) != 0) list.Add("64bit");
        if ((FeatureIncompat & IncompatInlineData) != 0) list.Add("inline_data");
        if ((FeatureIncompat & IncompatMetaBg) != 0) list.Add("meta_bg");
        if ((FeatureRoCompat & 0x400) != 0) list.Add("metadata_csum");
        if (HasSharedBlocks) list.Add("shared_blocks");
        return list;
    }

    // ---- inode access ----
    private sealed class Inode
    {
        public ushort Mode;
        public ulong Size;
        public uint Flags;
        public byte[] Block = Array.Empty<byte>();
        public bool IsDir => (Mode & 0xF000) == 0x4000;
        public bool IsLink => (Mode & 0xF000) == 0xA000;
        public bool IsFile => (Mode & 0xF000) == 0x8000;
    }

    private Inode ReadInode(uint ino)
    {
        if (ino == 0 || ino > InodesCount) throw new InvalidImageException($"Invalid inode number {ino}.");
        uint g = (ino - 1) / InodesPerGroup;
        uint idx = (ino - 1) % InodesPerGroup;
        if (g >= _inodeTables.Length) throw new InvalidImageException($"Inode {ino} is outside the group table.");
        var raw = new byte[InodeSize];
        _s.Position = _inodeTables[g] + (long)idx * InodeSize;
        _s.ReadExactly(raw);
        var r = raw.AsSpan();
        return new Inode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(r),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(r[0x04..]) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(r[0x6C..]) << 32),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(r[0x20..]),
            Block = r.Slice(0x28, 60).ToArray(),
        };
    }

    private byte[] ReadData(Inode ino, ulong maxBytes = 64 << 20)
    {
        ulong size = Math.Min(ino.Size, maxBytes);
        if ((ino.Flags & 0x10000000) != 0) // EXT4_INLINE_DATA_FL
        {
            if (ino.Size > 60) throw new NotSupportedException("Inline-data file larger than 60 bytes (stored in xattrs) is not supported.");
            return ino.Block.AsSpan(0, (int)size).ToArray();
        }
        if (ino.IsLink && ino.Size < 60 && (ino.Flags & 0x80000) == 0)
            return ino.Block.AsSpan(0, (int)ino.Size).ToArray(); // fast symlink
        var data = new byte[size];
        if (size == 0) return data;
        ulong nBlocks = (size + BlockSize - 1) / BlockSize;
        var map = new List<(ulong Logical, ulong Physical, ulong Count, bool Uninit)>();
        if ((ino.Flags & 0x80000) != 0) ExtentWalk(ino.Block, map, 0);
        else BlockMapWalk(ino.Block, nBlocks, map);
        foreach (var (logical, physical, count, uninit) in map)
        {
            if (logical >= nBlocks || uninit) continue;
            ulong c = Math.Min(count, nBlocks - logical);
            long dstOff = (long)(logical * BlockSize);
            long len = Math.Min((long)(c * BlockSize), (long)size - dstOff);
            if (len <= 0) continue;
            _s.Position = (long)(physical * BlockSize);
            _s.ReadExactly(data, (int)dstOff, (int)len);
        }
        return data;
    }

    private void ExtentWalk(byte[] node, List<(ulong, ulong, ulong, bool)> map, int depthGuard)
    {
        if (depthGuard > 8) throw new InvalidImageException("ext4 extent tree is too deep.");
        var s = node.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(s) != 0xF30A) throw new InvalidImageException("Bad ext4 extent header.");
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        for (int i = 0; i < entries; i++)
        {
            var e = s.Slice(12 + i * 12, 12);
            if (depth == 0)
            {
                uint lblk = BinaryPrimitives.ReadUInt32LittleEndian(e);
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                ulong start = ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                bool uninit = len > 32768;
                map.Add((lblk, start, (ulong)(uninit ? len - 32768 : len), uninit));
            }
            else
            {
                ulong leaf = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32);
                var child = new byte[BlockSize];
                _s.Position = (long)(leaf * BlockSize);
                _s.ReadExactly(child);
                ExtentWalk(child, map, depthGuard + 1);
            }
        }
    }

    private void BlockMapWalk(byte[] iblock, ulong nBlocks, List<(ulong, ulong, ulong, bool)> map)
    {
        uint per = BlockSize / 4;
        ulong logical = 0;
        for (int i = 0; i < 12 && logical < nBlocks; i++, logical++)
        {
            uint p = BinaryPrimitives.ReadUInt32LittleEndian(iblock.AsSpan(i * 4));
            if (p != 0) map.Add((logical, p, 1, false));
        }
        void Indirect(uint blk, int level)
        {
            if (blk == 0) { logical += (ulong)Math.Pow(per, level + 1); return; }
            var buf = new byte[BlockSize];
            _s.Position = (long)blk * BlockSize;
            _s.ReadExactly(buf);
            for (int i = 0; i < per && logical < nBlocks; i++)
            {
                uint p = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
                if (level == 0) { if (p != 0) map.Add((logical, p, 1, false)); logical++; }
                else Indirect(p, level - 1);
            }
        }
        for (int lvl = 0; lvl < 3 && logical < nBlocks; lvl++)
            Indirect(BinaryPrimitives.ReadUInt32LittleEndian(iblock.AsSpan((12 + lvl) * 4)), lvl);
    }

    private IEnumerable<(string Name, uint Inode)> ListDir(Inode dir)
    {
        var data = ReadData(dir);
        int pos = 0;
        bool inline = (dir.Flags & 0x10000000) != 0;
        if (inline) pos = 4; // parent inode first
        bool hasFileType = (FeatureIncompat & IncompatFiletype) != 0;
        while (pos + 8 <= data.Length)
        {
            uint ino = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
            int nameLen = hasFileType ? data[pos + 6] : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 6));
            if (recLen < 8) break;
            if (ino != 0 && nameLen > 0 && pos + 8 + nameLen <= data.Length)
                yield return (Encoding.UTF8.GetString(data, pos + 8, nameLen), ino);
            pos += recLen;
        }
    }

    private uint? Lookup(string path, int linkDepth = 0)
    {
        if (!CanReadFiles) return null;
        uint cur = 2; // root
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<uint> { 2 };
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 1) stack.RemoveAt(stack.Count - 1); cur = stack[^1]; continue; }
            var node = ReadInode(cur);
            if (!node.IsDir) return null;
            uint? next = null;
            foreach (var (name, ino) in ListDir(node)) if (name == part) { next = ino; break; }
            if (next == null) return null;
            var nextNode = ReadInode(next.Value);
            if (nextNode.IsLink)
            {
                if (linkDepth > 8) return null;
                var target = Encoding.UTF8.GetString(ReadData(nextNode));
                var prefix = target.StartsWith('/') ? "" : "/" + string.Join('/', parts.Take(i));
                var rest = string.Join('/', parts.Skip(i + 1));
                return Lookup($"{prefix}/{target}/{rest}", linkDepth + 1);
            }
            cur = next.Value;
            stack.Add(cur);
        }
        return cur;
    }

    public bool Exists(string path)
    {
        try { return Lookup(path) != null; } catch { return false; }
    }

    public bool IsDirectory(string path)
    {
        try { var i = Lookup(path); return i != null && ReadInode(i.Value).IsDir; } catch { return false; }
    }

    /// <summary>Reads a regular file, or returns null if it does not exist.</summary>
    public byte[]? ReadFile(string path, ulong maxBytes = 16 << 20)
    {
        var i = Lookup(path);
        if (i == null) return null;
        var node = ReadInode(i.Value);
        if (!node.IsFile) return null;
        return ReadData(node, maxBytes);
    }

    public IReadOnlyList<string> ListDirectory(string path)
    {
        var i = Lookup(path);
        if (i == null) return Array.Empty<string>();
        var node = ReadInode(i.Value);
        return node.IsDir ? ListDir(node).Select(x => x.Name).Where(n => n != "." && n != "..").ToList() : Array.Empty<string>();
    }
}

public static class BuildProp
{
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("import ")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }
}
