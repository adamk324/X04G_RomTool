using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.RomProfiles;

namespace X04G.Core.StorageAnalysis;

public sealed class SourcePartitionInfo
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required uint Attributes { get; init; }
    public required ulong Size { get; init; }
    public required int ExtentCount { get; init; }
    public AndroidImageInfo? Image { get; init; }
}

public sealed class PlannedPartition
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required uint Attributes { get; init; }
    /// <summary>"super:system" or a file path.</summary>
    public required string Source { get; init; }
    public bool IsReplacement { get; init; }
    public bool IsSparseFile { get; init; }
    /// <summary>Expanded image length in bytes (what is written into the partition).</summary>
    public required long ImageLength { get; init; }
    /// <summary>Partition size after alignment to the logical block size.</summary>
    public required ulong AlignedSize { get; init; }
    /// <summary>ext4 used bytes: the lower bound if the image is shrunk. Null if not ext4 / not shrinkable.</summary>
    public ulong? ShrinkLowerBound { get; init; }
    public bool Shrinkable { get; init; }
    public AndroidImageInfo? Image { get; init; }
}

public sealed class HostDiskInfo
{
    public required string Directory { get; init; }
    public long? FreeBytes { get; init; }
    public long RequiredBytes { get; init; }
    public List<(string What, long Bytes)> Breakdown { get; } = new();
}

public sealed class StorageReport
{
    public required RomProfile Profile { get; init; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    public required string SourcePath { get; init; }
    public bool SourceIsSuper { get; init; }
    public bool SourceIsSparse { get; init; }
    public long SourceFileLength { get; init; }
    public long SourceRawLength { get; init; }
    public LpMetadata? SourceMetadata { get; init; }
    public AndroidImageInfo? SourceFilesystem { get; init; }
    public List<SourcePartitionInfo> SourcePartitions { get; } = new();
    public List<PlannedPartition> Planned { get; } = new();
    public List<string> Removed { get; } = new();

    // Capacity
    public ulong? DeviceSize { get; init; }
    public ulong? MetadataReservedBytes { get; init; }
    public ulong? UsableBytes { get; init; }
    public ulong? CurrentAllocatedBytes { get; init; }
    public ulong? CurrentFreeBytes { get; init; }
    public ulong? RequiredBytes { get; set; }
    public ulong? RemainingBytes { get; set; }
    public ulong? AlignmentWasteBytes { get; set; }
    public long? EstimatedOutputBytes { get; set; }
    public bool EstimatedOutputIsUpperBound { get; set; }
    public LpLayoutResult? Layout { get; set; }
    public LpLayoutResult? ShrinkLayout { get; set; }
    public ulong? ShrinkRequiredLowerBound { get; set; }

    public HostDiskInfo? HostDisk { get; set; }
    public string? ToolchainName { get; set; }

    public List<Finding> CapacityFindings { get; } = new();
    public List<Finding> CompatibilityFindings { get; } = new();
    public List<Finding> InputFindings { get; } = new();

    public Verdict CapacityVerdict => Findings.Combine(CapacityFindings.Concat(InputFindings));
    public Verdict CompatibilityVerdict => CompatibilityFindings.Count == 0 ? Verdict.Pass : Findings.Combine(CompatibilityFindings);
    public Verdict Overall => (Verdict)Math.Max((int)CapacityVerdict, (int)CompatibilityVerdict);

    public bool BlocksExecution => CapacityVerdict == Verdict.Fail || InputFindings.Any(f => f.Severity == Severity.Fail);

    public string ToText()
    {
        var sb = new StringBuilder();
        string S(ulong? v) => v is ulong x ? ByteSize.WithExact(x) : "unknown";
        sb.AppendLine("X04G Windows ROM Toolkit — storage & compatibility report");
        sb.AppendLine($"Created: {CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Target: {Profile.Name} — {Profile.AndroidVersion} [{Profile.StatusText}]");
        sb.AppendLine();
        sb.AppendLine($"RESULT: capacity {CapacityVerdict.ToString().ToUpperInvariant()}, compatibility {CompatibilityVerdict.ToString().ToUpperInvariant()}, overall {Overall.ToString().ToUpperInvariant()}");
        sb.AppendLine();
        sb.AppendLine($"Source: {SourcePath}");
        sb.AppendLine($"  {(SourceIsSparse ? "sparse" : "raw")}, file {ByteSize.WithExact(SourceFileLength)}, expanded {ByteSize.WithExact(SourceRawLength)}");
        if (SourceMetadata is { } md)
        {
            var d = md.SuperDevice;
            sb.AppendLine($"  LP metadata v{md.MajorVersion}.{md.MinorVersion}, {md.Geometry.MetadataSlotCount} slot(s), max {md.Geometry.MetadataMaxSize} bytes, block {md.Geometry.LogicalBlockSize}, alignment {d.Alignment} (+{d.AlignmentOffset}), block device '{d.PartitionName}'");
        }
        sb.AppendLine();
        sb.AppendLine("Device storage (super partition)");
        sb.AppendLine($"  Total super size:          {S(DeviceSize)}");
        sb.AppendLine($"  Metadata reserved:         {S(MetadataReservedBytes)}");
        sb.AppendLine($"  Usable for partitions:     {S(UsableBytes)}");
        sb.AppendLine($"  Currently allocated:       {S(CurrentAllocatedBytes)}");
        sb.AppendLine($"  Currently free:            {S(CurrentFreeBytes)}");
        sb.AppendLine($"  Required (new layout):     {S(RequiredBytes)}");
        if (AlignmentWasteBytes is > 0) sb.AppendLine($"    incl. alignment gaps:    {S(AlignmentWasteBytes)}");
        sb.AppendLine($"  Remaining after layout:    {S(RemainingBytes)}");
        if (ShrinkRequiredLowerBound is ulong lb) sb.AppendLine($"  Required if shrunk (lower bound): {S(lb)}");
        if (EstimatedOutputBytes is long eo) sb.AppendLine($"  Estimated output file:     {(EstimatedOutputIsUpperBound ? "≤ " : "")}{ByteSize.WithExact(eo)}");
        sb.AppendLine();
        if (SourcePartitions.Count > 0)
        {
            sb.AppendLine("Current partitions");
            foreach (var p in SourcePartitions)
                sb.AppendLine($"  {p.Name,-12} group {p.Group,-10} {ByteSize.WithExact(p.Size)}{(p.Image != null ? $"  [{Describe(p.Image)}]" : "")}");
            sb.AppendLine();
        }
        if (Planned.Count > 0)
        {
            sb.AppendLine("Planned partitions");
            foreach (var p in Planned)
                sb.AppendLine($"  {p.Name,-12} {ByteSize.WithExact(p.AlignedSize)} from {p.Source}{(p.ShrinkLowerBound is ulong u ? $" (ext4 used {ByteSize.Format(u)})" : "")}");
            foreach (var r in Removed) sb.AppendLine($"  {r,-12} REMOVED");
            sb.AppendLine();
        }
        if (HostDisk is { } h)
        {
            sb.AppendLine($"Windows drive: {h.Directory}");
            sb.AppendLine($"  Free: {(h.FreeBytes is long fb ? ByteSize.WithExact(fb) : "unknown")}, needed: {ByteSize.WithExact(h.RequiredBytes)}");
            foreach (var (w, b) in h.Breakdown) sb.AppendLine($"    {w}: {ByteSize.Format(b)}");
            sb.AppendLine();
        }
        sb.AppendLine($"ext4 tools: {ToolchainName ?? "not available"}");
        sb.AppendLine();
        foreach (var (title, list) in new[] { ("Input checks", InputFindings), ("Capacity checks", CapacityFindings), ("Compatibility checks", CompatibilityFindings) })
        {
            if (list.Count == 0) continue;
            sb.AppendLine(title);
            foreach (var f in list) sb.AppendLine($"  [{f.Severity.ToString().ToUpperInvariant(),-7}] {f.Message}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Describe(AndroidImageInfo i)
    {
        var parts = new List<string> { i.FileSystem.ToString().ToLowerInvariant() };
        if (i.Ext4UsedBytes is ulong u && i.Ext4FilesystemBytes is ulong t) parts.Add($"{ByteSize.Format(u)} used of {ByteSize.Format(t)}");
        if (i.Release != null) parts.Add($"Android {i.Release}");
        if (i.Sdk != null) parts.Add($"SDK {i.Sdk}");
        if (i.AbiList != null) parts.Add(i.AbiList);
        if (i.VndkVersion != null) parts.Add($"VNDK {i.VndkVersion}");
        if (i.SystemAsRoot == true) parts.Add("system-as-root");
        return string.Join(", ", parts);
    }
}
