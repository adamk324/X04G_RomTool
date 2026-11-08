using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

/// <summary>
/// Constants of the Android "liblp" logical partition (super / dynamic partitions) format,
/// taken from AOSP android10-release fs_mgr/liblp/include/liblp/metadata_format.h.
/// </summary>
public static class LpFormat
{
    public const uint GeometryMagic = 0x616c4467;
    public const int GeometrySize = 4096;
    public const int GeometryStructSize = 52;
    public const uint HeaderMagic = 0x414C5030;
    public const ushort MajorVersion = 10;
    /// <summary>Android 10 liblp supports minor version 0 only. Newer readers accept 0..2.</summary>
    public const ushort MinorVersionAndroid10 = 0;
    public const ushort MinorVersionMaxKnown = 2;
    public const int HeaderSizeV1_0 = 128;
    public const int HeaderSizeV1_2 = 256;
    public const int PartitionReservedBytes = 4096;
    public const int SectorSize = 512;
    public const int PartitionEntrySize = 52;
    public const int ExtentEntrySize = 24;
    public const int GroupEntrySize = 48;
    public const int BlockDeviceEntrySize = 64;
    public const int NameLength = 36;

    public const uint AttrReadOnly = 1;
    public const uint AttrSlotSuffixed = 2;
    public const uint AttrUpdated = 4;
    public const uint AttrDisabled = 8;

    public const uint TargetLinear = 0;
    public const uint TargetZero = 1;

    /// <summary>lpmake defaults (Android 10 builder.h): 1 MiB partition alignment, 4 KiB logical blocks.</summary>
    public const uint DefaultAlignment = 1024 * 1024;
    public const uint DefaultBlockSize = 4096;

    public static long PrimaryGeometryOffset => PartitionReservedBytes;
    public static long BackupGeometryOffset => PartitionReservedBytes + GeometrySize;
    public static long PrimaryMetadataOffset(LpGeometry g, uint slot) =>
        PartitionReservedBytes + GeometrySize * 2L + (long)g.MetadataMaxSize * slot;
    public static long BackupMetadataOffset(LpGeometry g, uint slot) =>
        PartitionReservedBytes + GeometrySize * 2L + (long)g.MetadataMaxSize * g.MetadataSlotCount + (long)g.MetadataMaxSize * slot;

    /// <summary>Bytes reserved at the start of super for geometry and all metadata copies (utility.cpp GetTotalMetadataSize).</summary>
    public static ulong TotalMetadataSize(uint metadataMaxSize, uint slots) =>
        PartitionReservedBytes + (GeometrySize + (ulong)metadataMaxSize * slots) * 2;

    /// <summary>liblp AlignTo(base, alignment, alignment_offset).</summary>
    public static ulong AlignTo(ulong value, uint alignment, uint alignmentOffset = 0)
    {
        if (alignment == 0) return value + 0;
        ulong r = value % alignment;
        ulong aligned = (r == 0 ? value : value + (alignment - r)) + alignmentOffset;
        if (alignmentOffset != 0 && aligned >= alignment && aligned - alignment >= value) return aligned - alignment;
        return aligned;
    }
}

public sealed class LpGeometry
{
    public uint StructSize { get; init; } = LpFormat.GeometryStructSize;
    public uint MetadataMaxSize { get; init; }
    public uint MetadataSlotCount { get; init; }
    public uint LogicalBlockSize { get; init; }
}

public sealed class LpPartition
{
    public string Name { get; init; } = "";
    public uint Attributes { get; init; }
    public uint FirstExtentIndex { get; init; }
    public uint NumExtents { get; init; }
    public uint GroupIndex { get; init; }
    public bool IsReadOnly => (Attributes & LpFormat.AttrReadOnly) != 0;
}

public sealed class LpExtent
{
    public ulong NumSectors { get; init; }
    public uint TargetType { get; init; }
    public ulong TargetData { get; init; }
    public uint TargetSource { get; init; }
    public ulong Bytes => NumSectors * LpFormat.SectorSize;
}

public sealed class LpGroup
{
    public string Name { get; init; } = "";
    public uint Flags { get; init; }
    public ulong MaximumSize { get; init; }
}

public sealed class LpBlockDevice
{
    public ulong FirstLogicalSector { get; init; }
    public uint Alignment { get; init; }
    public uint AlignmentOffset { get; init; }
    public ulong Size { get; init; }
    public string PartitionName { get; init; } = "";
    public uint Flags { get; init; }
}

public sealed class LpMetadata
{
    public LpGeometry Geometry { get; init; } = new();
    public ushort MajorVersion { get; init; } = LpFormat.MajorVersion;
    public ushort MinorVersion { get; init; } = LpFormat.MinorVersionAndroid10;
    public uint HeaderSize { get; init; } = LpFormat.HeaderSizeV1_0;
    public uint HeaderFlags { get; init; }
    public List<LpPartition> Partitions { get; init; } = new();
    public List<LpExtent> Extents { get; init; } = new();
    public List<LpGroup> Groups { get; init; } = new();
    public List<LpBlockDevice> BlockDevices { get; init; } = new();

    public LpBlockDevice SuperDevice => BlockDevices.Count > 0 ? BlockDevices[0] : throw new InvalidImageException("Metadata has no block device.");

    public IEnumerable<LpExtent> ExtentsOf(LpPartition p)
    {
        for (uint i = 0; i < p.NumExtents; i++) yield return Extents[(int)(p.FirstExtentIndex + i)];
    }

    public ulong SizeOf(LpPartition p) => ExtentsOf(p).Aggregate(0UL, (a, e) => a + e.Bytes);

    public LpPartition? FindPartition(string name) => Partitions.FirstOrDefault(p => p.Name == name);

    public string GroupNameOf(LpPartition p) => p.GroupIndex < Groups.Count ? Groups[(int)p.GroupIndex].Name : "?";

    public ulong UsableBytes => SuperDevice.Size - SuperDevice.FirstLogicalSector * LpFormat.SectorSize;

    public ulong AllocatedBytes => Partitions.Aggregate(0UL, (a, p) => a + SizeOf(p));

    /// <summary>Highest byte offset (exclusive) used by any extent, or the first logical byte if nothing is allocated.</summary>
    public ulong HighestUsedByte =>
        Extents.Where(e => e.TargetType == LpFormat.TargetLinear)
               .Select(e => (e.TargetData + e.NumSectors) * LpFormat.SectorSize)
               .DefaultIfEmpty(SuperDevice.FirstLogicalSector * LpFormat.SectorSize).Max();
}

internal static class LpNames
{
    public static string Read(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        if (n < 0) n = b.Length;
        return Encoding.ASCII.GetString(b[..n]);
    }

    public static void Write(Span<byte> dst, string name)
    {
        if (name.Length > LpFormat.NameLength) throw new ArgumentException($"Name too long for LP metadata: {name}");
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ArgumentException($"Invalid character in LP name '{name}'. Only A-Z, a-z, 0-9 and _ are allowed.");
        dst[..LpFormat.NameLength].Clear();
        Encoding.ASCII.GetBytes(name, dst);
    }
}

public sealed class LpReadResult
{
    public required LpMetadata Metadata { get; init; }
    public List<string> Warnings { get; } = new();
    public bool UsedBackupGeometry { get; set; }
    public bool UsedBackupMetadata { get; set; }
}

/// <summary>Parses and validates LP metadata (replaces lpdump / the reading part of lpunpack).</summary>
public static class LpMetadataReader
{
    public static bool LooksLikeSuper(Stream raw)
    {
        if (raw.Length < LpFormat.PartitionReservedBytes + LpFormat.GeometrySize) return false;
        Span<byte> b = stackalloc byte[4];
        raw.Position = LpFormat.PrimaryGeometryOffset;
        if (raw.ReadAtLeast(b, 4, false) < 4) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) == LpFormat.GeometryMagic) return true;
        raw.Position = LpFormat.BackupGeometryOffset;
        if (raw.ReadAtLeast(b, 4, false) < 4) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(b) == LpFormat.GeometryMagic;
    }

    public static LpReadResult Read(Stream raw, uint slot = 0)
    {
        if (raw.Length < LpFormat.PartitionReservedBytes + 2L * LpFormat.GeometrySize)
            throw new InvalidImageException("File is too small to be a super partition image.");

        string? primaryError;
        LpGeometry? geometry = TryReadGeometry(raw, LpFormat.PrimaryGeometryOffset, out primaryError);
        bool backupGeom = false;
        if (geometry == null)
        {
            geometry = TryReadGeometry(raw, LpFormat.BackupGeometryOffset, out var backupError);
            if (geometry == null)
                throw new InvalidImageException(
                    "No valid logical-partition (super) geometry found. This does not look like a super image." +
                    $" Primary: {primaryError}. Backup: {backupError}.");
            backupGeom = true;
        }
        if (slot >= geometry.MetadataSlotCount) throw new InvalidImageException($"Metadata slot {slot} does not exist (slot count {geometry.MetadataSlotCount}).");

        var primary = TryReadMetadata(raw, geometry, LpFormat.PrimaryMetadataOffset(geometry, slot), out var pErr);
        bool backupMeta = false;
        if (primary == null)
        {
            primary = TryReadMetadata(raw, geometry, LpFormat.BackupMetadataOffset(geometry, slot), out var bErr);
            if (primary == null)
                throw new InvalidImageException($"Super geometry is valid, but no valid metadata copy was found. Primary: {pErr}. Backup: {bErr}.");
            backupMeta = true;
        }

        var result = new LpReadResult { Metadata = primary, UsedBackupGeometry = backupGeom, UsedBackupMetadata = backupMeta };
        if (backupGeom) result.Warnings.Add($"Primary geometry is invalid ({primaryError}); the backup copy was used.");
        if (backupMeta) result.Warnings.Add($"Primary metadata is invalid ({pErr}); the backup copy was used.");
        if (!backupMeta)
        {
            var backup = TryReadMetadata(raw, geometry, LpFormat.BackupMetadataOffset(geometry, slot), out var bErr2);
            if (backup == null) result.Warnings.Add($"Backup metadata copy is invalid ({bErr2}). The primary copy is intact.");
        }
        ValidateLayout(primary, raw.Length, result.Warnings);
        return result;
    }

    private static LpGeometry? TryReadGeometry(Stream raw, long offset, out string? error)
    {
        var buf = new byte[LpFormat.GeometrySize];
        raw.Position = offset;
        if (raw.ReadAtLeast(buf, buf.Length, false) < buf.Length) { error = "truncated"; return null; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != LpFormat.GeometryMagic) { error = "bad magic"; return null; }
        uint structSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        if (structSize != LpFormat.GeometryStructSize) { error = $"unsupported struct size {structSize}"; return null; }
        var stored = buf.AsSpan(8, 32).ToArray();
        var tmp = buf.AsSpan(0, (int)structSize).ToArray();
        tmp.AsSpan(8, 32).Clear();
        if (!SHA256.HashData(tmp).AsSpan().SequenceEqual(stored)) { error = "checksum mismatch"; return null; }
        var g = new LpGeometry
        {
            StructSize = structSize,
            MetadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40)),
            MetadataSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(44)),
            LogicalBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(48)),
        };
        if (g.MetadataSlotCount == 0) { error = "slot count is 0"; return null; }
        if (g.MetadataMaxSize == 0 || g.MetadataMaxSize % LpFormat.SectorSize != 0) { error = "metadata max size is not sector aligned"; return null; }
        if (g.LogicalBlockSize == 0 || g.LogicalBlockSize % LpFormat.SectorSize != 0) { error = "logical block size is not sector aligned"; return null; }
        error = null;
        return g;
    }

    private static LpMetadata? TryReadMetadata(Stream raw, LpGeometry g, long offset, out string? error)
    {
        if (offset + g.MetadataMaxSize > raw.Length) { error = "outside of file"; return null; }
        var buf = new byte[g.MetadataMaxSize];
        raw.Position = offset;
        if (raw.ReadAtLeast(buf, buf.Length, false) < buf.Length) { error = "truncated"; return null; }
        var s = buf.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != LpFormat.HeaderMagic) { error = "bad header magic"; return null; }
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        if (major != LpFormat.MajorVersion || minor > LpFormat.MinorVersionMaxKnown) { error = $"unsupported version {major}.{minor}"; return null; }
        uint expectedHeader = minor >= 2 ? (uint)LpFormat.HeaderSizeV1_2 : LpFormat.HeaderSizeV1_0;
        if (headerSize != expectedHeader) { error = $"unexpected header size {headerSize}"; return null; }
        var hdr = s[..(int)headerSize].ToArray();
        var storedHdrSum = hdr.AsSpan(12, 32).ToArray();
        hdr.AsSpan(12, 32).Clear();
        if (!SHA256.HashData(hdr).AsSpan().SequenceEqual(storedHdrSum)) { error = "header checksum mismatch"; return null; }
        uint tablesSize = BinaryPrimitives.ReadUInt32LittleEndian(s[44..]);
        if (headerSize + (ulong)tablesSize > g.MetadataMaxSize) { error = "tables exceed metadata size"; return null; }
        var tables = s.Slice((int)headerSize, (int)tablesSize);
        if (!SHA256.HashData(tables).AsSpan().SequenceEqual(s.Slice(48, 32))) { error = "tables checksum mismatch"; return null; }

        static (uint off, uint num, uint size) Desc(byte[] m, int at) => (
            BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at)),
            BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 8)));
        var dp = Desc(buf, 80); var de = Desc(buf, 92); var dg = Desc(buf, 104); var db = Desc(buf, 116);
        foreach (var (d, sz, nm) in new[] { (dp, LpFormat.PartitionEntrySize, "partition"), (de, LpFormat.ExtentEntrySize, "extent"), (dg, LpFormat.GroupEntrySize, "group"), (db, LpFormat.BlockDeviceEntrySize, "block device") })
        {
            if (d.size != sz) { error = $"{nm} table entry size {d.size} != {sz}"; return null; }
            if ((ulong)d.off + (ulong)d.num * d.size > tablesSize) { error = $"{nm} table out of bounds"; return null; }
        }

        var md = new LpMetadata
        {
            Geometry = g,
            MajorVersion = major,
            MinorVersion = minor,
            HeaderSize = headerSize,
            HeaderFlags = minor >= 2 ? BinaryPrimitives.ReadUInt32LittleEndian(s[128..]) : 0,
        };
        for (int i = 0; i < dp.num; i++)
        {
            var e = tables.Slice((int)(dp.off + i * dp.size), (int)dp.size);
            md.Partitions.Add(new LpPartition
            {
                Name = LpNames.Read(e[..36]),
                Attributes = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]),
                FirstExtentIndex = BinaryPrimitives.ReadUInt32LittleEndian(e[40..]),
                NumExtents = BinaryPrimitives.ReadUInt32LittleEndian(e[44..]),
                GroupIndex = BinaryPrimitives.ReadUInt32LittleEndian(e[48..]),
            });
        }
        for (int i = 0; i < de.num; i++)
        {
            var e = tables.Slice((int)(de.off + i * de.size), (int)de.size);
            md.Extents.Add(new LpExtent
            {
                NumSectors = BinaryPrimitives.ReadUInt64LittleEndian(e),
                TargetType = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                TargetData = BinaryPrimitives.ReadUInt64LittleEndian(e[12..]),
                TargetSource = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]),
            });
        }
        for (int i = 0; i < dg.num; i++)
        {
            var e = tables.Slice((int)(dg.off + i * dg.size), (int)dg.size);
            md.Groups.Add(new LpGroup
            {
                Name = LpNames.Read(e[..36]),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]),
                MaximumSize = BinaryPrimitives.ReadUInt64LittleEndian(e[40..]),
            });
        }
        for (int i = 0; i < db.num; i++)
        {
            var e = tables.Slice((int)(db.off + i * db.size), (int)db.size);
            md.BlockDevices.Add(new LpBlockDevice
            {
                FirstLogicalSector = BinaryPrimitives.ReadUInt64LittleEndian(e),
                Alignment = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                AlignmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]),
                Size = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]),
                PartitionName = LpNames.Read(e.Slice(24, 36)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(e[60..]),
            });
        }

        // Structural validation (reader.cpp ParseMetadata)
        if (md.BlockDevices.Count == 0) { error = "no super block device"; return null; }
        foreach (var p in md.Partitions)
        {
            if ((ulong)p.FirstExtentIndex + p.NumExtents > (ulong)md.Extents.Count) { error = $"partition {p.Name} has invalid extent list"; return null; }
            if (p.GroupIndex >= md.Groups.Count) { error = $"partition {p.Name} has invalid group index"; return null; }
        }
        foreach (var e in md.Extents)
            if (e.TargetType == LpFormat.TargetLinear && e.TargetSource >= md.BlockDevices.Count) { error = "extent references an unknown block device"; return null; }
        ulong reserved = LpFormat.TotalMetadataSize(g.MetadataMaxSize, g.MetadataSlotCount);
        if (reserved > md.SuperDevice.FirstLogicalSector * LpFormat.SectorSize) { error = "metadata overlaps partition contents"; return null; }

        error = null;
        return md;
    }

    private static void ValidateLayout(LpMetadata md, long imageLength, List<string> warnings)
    {
        var dev = md.SuperDevice;
        if (md.BlockDevices.Count > 1)
            warnings.Add($"Metadata describes {md.BlockDevices.Count} block devices (retrofit layout). Only the first ('{dev.PartitionName}') is contained in this file.");
        if ((ulong)imageLength != dev.Size)
            warnings.Add($"Image length ({imageLength:N0} bytes) differs from the super size recorded in the metadata ({dev.Size:N0} bytes).");
        var linear = md.Extents.Where(e => e.TargetType == LpFormat.TargetLinear && e.TargetSource == 0)
                               .OrderBy(e => e.TargetData).ToList();
        for (int i = 0; i < linear.Count; i++)
        {
            var e = linear[i];
            if (e.TargetData < dev.FirstLogicalSector) warnings.Add($"An extent starts at sector {e.TargetData}, inside the metadata area.");
            if ((e.TargetData + e.NumSectors) * LpFormat.SectorSize > dev.Size) warnings.Add($"An extent ends beyond the super device size.");
            if (i > 0 && linear[i - 1].TargetData + linear[i - 1].NumSectors > e.TargetData) warnings.Add("Two extents overlap.");
        }
        foreach (var grp in md.Groups.Where(g => g.MaximumSize > 0))
        {
            ulong used = md.Partitions.Where(p => md.GroupNameOf(p) == grp.Name).Aggregate(0UL, (a, p) => a + md.SizeOf(p));
            if (used > grp.MaximumSize) warnings.Add($"Group '{grp.Name}' uses {used:N0} bytes but its maximum is {grp.MaximumSize:N0}.");
        }
    }
}

/// <summary>Serializes LP metadata exactly like AOSP writer.cpp (SerializeGeometry / SerializeMetadata).</summary>
public static class LpMetadataWriter
{
    public static byte[] SerializeGeometry(LpGeometry g)
    {
        var blob = new byte[LpFormat.GeometrySize];
        var s = blob.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, LpFormat.GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], LpFormat.GeometryStructSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], g.MetadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], g.MetadataSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], g.LogicalBlockSize);
        SHA256.HashData(s[..LpFormat.GeometryStructSize]).CopyTo(s[8..]);
        return blob;
    }

    public static byte[] SerializeMetadata(LpMetadata md)
    {
        if (md.MajorVersion != LpFormat.MajorVersion || md.MinorVersion != LpFormat.MinorVersionAndroid10)
            throw new NotSupportedException("Only LP metadata version 10.0 (Android 10) is written.");
        var tables = new byte[md.Partitions.Count * LpFormat.PartitionEntrySize + md.Extents.Count * LpFormat.ExtentEntrySize
                              + md.Groups.Count * LpFormat.GroupEntrySize + md.BlockDevices.Count * LpFormat.BlockDeviceEntrySize];
        var t = tables.AsSpan();
        int pos = 0;
        uint offP = 0;
        foreach (var p in md.Partitions)
        {
            var e = t.Slice(pos, LpFormat.PartitionEntrySize);
            LpNames.Write(e, p.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(e[36..], p.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(e[40..], p.FirstExtentIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e[44..], p.NumExtents);
            BinaryPrimitives.WriteUInt32LittleEndian(e[48..], p.GroupIndex);
            pos += LpFormat.PartitionEntrySize;
        }
        uint offE = (uint)pos;
        foreach (var x in md.Extents)
        {
            var e = t.Slice(pos, LpFormat.ExtentEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(e, x.NumSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], x.TargetType);
            BinaryPrimitives.WriteUInt64LittleEndian(e[12..], x.TargetData);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], x.TargetSource);
            pos += LpFormat.ExtentEntrySize;
        }
        uint offG = (uint)pos;
        foreach (var g in md.Groups)
        {
            var e = t.Slice(pos, LpFormat.GroupEntrySize);
            LpNames.Write(e, g.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(e[36..], g.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], g.MaximumSize);
            pos += LpFormat.GroupEntrySize;
        }
        uint offB = (uint)pos;
        foreach (var b in md.BlockDevices)
        {
            var e = t.Slice(pos, LpFormat.BlockDeviceEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(e, b.FirstLogicalSector);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], b.Alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], b.AlignmentOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], b.Size);
            LpNames.Write(e.Slice(24, 36), b.PartitionName);
            BinaryPrimitives.WriteUInt32LittleEndian(e[60..], b.Flags);
            pos += LpFormat.BlockDeviceEntrySize;
        }

        var header = new byte[LpFormat.HeaderSizeV1_0];
        var h = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h, LpFormat.HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], LpFormat.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], LpFormat.MinorVersionAndroid10);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], LpFormat.HeaderSizeV1_0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], (uint)tables.Length);
        SHA256.HashData(tables).CopyTo(h[48..]);
        static void Desc(byte[] hb, int at, uint off, int num, int size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hb.AsSpan(at), off);
            BinaryPrimitives.WriteUInt32LittleEndian(hb.AsSpan(at + 4), (uint)num);
            BinaryPrimitives.WriteUInt32LittleEndian(hb.AsSpan(at + 8), (uint)size);
        }
        Desc(header, 80, offP, md.Partitions.Count, LpFormat.PartitionEntrySize);
        Desc(header, 92, offE, md.Extents.Count, LpFormat.ExtentEntrySize);
        Desc(header, 104, offG, md.Groups.Count, LpFormat.GroupEntrySize);
        Desc(header, 116, offB, md.BlockDevices.Count, LpFormat.BlockDeviceEntrySize);
        SHA256.HashData(header).CopyTo(h[12..]);

        var blob = new byte[header.Length + tables.Length];
        header.CopyTo(blob, 0);
        tables.CopyTo(blob, header.Length);
        if (blob.Length > md.Geometry.MetadataMaxSize)
            throw new ToolkitException($"Logical partition metadata is too large ({blob.Length} > {md.Geometry.MetadataMaxSize} bytes).");
        return blob;
    }
}
