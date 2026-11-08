using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

public sealed record LpPartitionRequest(string Name, string Group, uint Attributes, ulong Size);
public sealed record LpGroupRequest(string Name, ulong MaximumSize);

public sealed class LpLayoutRequest
{
    public ulong DeviceSize { get; init; }
    public string SuperName { get; init; } = "super";
    public uint MetadataMaxSize { get; init; } = 65536;
    public uint MetadataSlotCount { get; init; } = 1;
    public uint LogicalBlockSize { get; init; } = LpFormat.DefaultBlockSize;
    public uint Alignment { get; init; } = LpFormat.DefaultAlignment;
    public uint AlignmentOffset { get; init; }
    public List<LpGroupRequest> Groups { get; init; } = new();
    public List<LpPartitionRequest> Partitions { get; init; } = new();
}

public sealed class LpLayoutResult
{
    public LpMetadata? Metadata { get; init; }
    public string? Error { get; init; }
    public bool Success => Metadata != null;
    public ulong FirstLogicalByte { get; init; }
    /// <summary>End (exclusive, bytes) of the last allocated extent.</summary>
    public ulong EndOfAllocation { get; init; }
    /// <summary>Free bytes after allocation (device size minus end of allocation, not counting alignment gaps elsewhere).</summary>
    public ulong FreeAfterAllocation { get; init; }
    /// <summary>Bytes lost to alignment between partitions.</summary>
    public ulong AlignmentWaste { get; init; }
}

/// <summary>
/// Re-implementation of the parts of AOSP liblp MetadataBuilder (android10-release builder.cpp) that
/// lpmake uses: reserve metadata area, add groups and partitions in order, allocate each partition
/// from the free regions with the block device alignment. Producing the same layout as lpmake lets the
/// storage checker predict the exact result before anything is written.
/// </summary>
public static class LpLayoutPlanner
{
    private const string DefaultGroup = "default";

    public static LpLayoutResult Plan(LpLayoutRequest r)
    {
        LpLayoutResult Fail(string msg) => new() { Error = msg };

        if (r.LogicalBlockSize == 0 || r.LogicalBlockSize % LpFormat.SectorSize != 0) return Fail("Logical block size must be a multiple of 512.");
        if (r.DeviceSize % r.LogicalBlockSize != 0) return Fail($"Super size {r.DeviceSize:N0} is not a multiple of the logical block size {r.LogicalBlockSize}.");
        if (r.Alignment % LpFormat.SectorSize != 0 || r.AlignmentOffset % LpFormat.SectorSize != 0) return Fail("Alignment must be sector aligned.");
        if (r.AlignmentOffset > r.Alignment) return Fail("Alignment offset is greater than alignment.");
        if (r.MetadataSlotCount == 0) return Fail("Metadata slot count must be at least 1.");
        uint metadataMax = (uint)ByteSize.AlignUp(r.MetadataMaxSize, LpFormat.SectorSize);

        ulong totalReserved = LpFormat.TotalMetadataSize(metadataMax, r.MetadataSlotCount);
        if (r.DeviceSize < totalReserved) return Fail("The super partition is too small to hold its own metadata.");
        ulong freeStart = (r.Alignment != 0 || r.AlignmentOffset != 0)
            ? LpFormat.AlignTo(totalReserved, r.Alignment, r.AlignmentOffset)
            : LpFormat.AlignTo(totalReserved, r.LogicalBlockSize);
        ulong firstLogicalSector = freeStart / LpFormat.SectorSize;
        if (r.DeviceSize < firstLogicalSector * LpFormat.SectorSize + r.LogicalBlockSize)
            return Fail($"Device must be at least {firstLogicalSector * LpFormat.SectorSize + r.LogicalBlockSize:N0} bytes.");

        var groups = new List<LpGroup> { new() { Name = DefaultGroup, MaximumSize = 0 } };
        foreach (var g in r.Groups)
        {
            if (g.Name == DefaultGroup) continue;
            if (groups.Any(x => x.Name == g.Name)) return Fail($"Duplicate group '{g.Name}'.");
            groups.Add(new LpGroup { Name = g.Name, MaximumSize = g.MaximumSize });
        }

        var dev = new LpBlockDevice
        {
            FirstLogicalSector = firstLogicalSector,
            Alignment = r.Alignment,
            AlignmentOffset = r.AlignmentOffset,
            Size = r.DeviceSize,
            PartitionName = r.SuperName,
        };

        ulong AlignSector(ulong sector)
        {
            ulong lba = sector * LpFormat.SectorSize;
            ulong aligned = LpFormat.AlignTo(lba, dev.Alignment, dev.AlignmentOffset);
            return ByteSize.AlignUp(aligned, LpFormat.SectorSize) / LpFormat.SectorSize;
        }

        // Allocated intervals [start, end) in sectors.
        var used = new List<(ulong Start, ulong End)>();
        var partitions = new List<LpPartition>();
        var extents = new List<LpExtent>();
        var groupUse = new Dictionary<string, ulong>();
        ulong sectorsPerBlock = r.LogicalBlockSize / LpFormat.SectorSize;
        ulong lastSector = r.DeviceSize / LpFormat.SectorSize;

        foreach (var p in r.Partitions)
        {
            if (partitions.Any(x => x.Name == p.Name)) return Fail($"Duplicate partition '{p.Name}'.");
            int gi = groups.FindIndex(g => g.Name == p.Group);
            if (gi < 0) return Fail($"Partition '{p.Name}' references unknown group '{p.Group}'.");
            ulong aligned = ByteSize.AlignUp(p.Size, r.LogicalBlockSize);

            var grp = groups[gi];
            groupUse.TryGetValue(grp.Name, out var gUsed);
            if (grp.MaximumSize > 0 && aligned > 0 && (gUsed >= grp.MaximumSize || grp.MaximumSize - gUsed < aligned))
                return Fail($"Partition '{p.Name}' ({aligned:N0} bytes) does not fit in group '{grp.Name}': {gUsed:N0} of {grp.MaximumSize:N0} bytes already used.");

            // Free regions (builder.cpp GetFreeRegions + ExtentsToFreeList)
            var points = new List<(ulong Start, ulong End)>(used) { (firstLogicalSector, firstLogicalSector), (lastSector, lastSector) };
            points.Sort();
            var free = new List<(ulong Start, ulong End)>();
            for (int i = 1; i < points.Count; i++)
            {
                ulong a = AlignSector(points[i - 1].End);
                if (a >= points[i].Start) continue;
                free.Add((a, points[i].Start));
            }

            uint first = (uint)extents.Count;
            ulong needed = aligned / LpFormat.SectorSize;
            var newExtents = new List<LpExtent>();
            foreach (var region in free)
            {
                if (needed == 0) break;
                ulong len = region.End - region.Start;
                len = len / sectorsPerBlock * sectorsPerBlock;
                if (len == 0) continue;
                ulong take = Math.Min(needed, len);
                newExtents.Add(new LpExtent { NumSectors = take, TargetType = LpFormat.TargetLinear, TargetData = region.Start, TargetSource = 0 });
                needed -= take;
            }
            if (needed > 0)
                return Fail($"Not enough free space in super for partition '{p.Name}' ({aligned:N0} bytes requested; {needed * LpFormat.SectorSize:N0} bytes could not be allocated).");

            foreach (var e in newExtents)
            {
                extents.Add(e);
                used.Add((e.TargetData, e.TargetData + e.NumSectors));
            }
            partitions.Add(new LpPartition
            {
                Name = p.Name,
                Attributes = p.Attributes,
                FirstExtentIndex = first,
                NumExtents = (uint)newExtents.Count,
                GroupIndex = (uint)gi,
            });
            groupUse[grp.Name] = gUsed + aligned;
        }

        var md = new LpMetadata
        {
            Geometry = new LpGeometry { MetadataMaxSize = metadataMax, MetadataSlotCount = r.MetadataSlotCount, LogicalBlockSize = r.LogicalBlockSize },
            Partitions = partitions,
            Extents = extents,
            Groups = groups,
            BlockDevices = new List<LpBlockDevice> { dev },
        };
        // Ensure it serializes within metadata_max_size
        try { LpMetadataWriter.SerializeMetadata(md); }
        catch (Exception ex) { return Fail(ex.Message); }

        ulong end = used.Count == 0 ? firstLogicalSector * LpFormat.SectorSize : used.Max(u => u.End) * LpFormat.SectorSize;
        ulong allocated = used.Aggregate(0UL, (a, u) => a + (u.End - u.Start) * LpFormat.SectorSize);
        return new LpLayoutResult
        {
            Metadata = md,
            FirstLogicalByte = firstLogicalSector * LpFormat.SectorSize,
            EndOfAllocation = end,
            FreeAfterAllocation = r.DeviceSize - end,
            AlignmentWaste = end - firstLogicalSector * LpFormat.SectorSize - allocated,
        };
    }
}
