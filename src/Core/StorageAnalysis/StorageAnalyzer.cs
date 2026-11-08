using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;

namespace X04G.Core.StorageAnalysis;

/// <summary>
/// Read-only analysis: opens the inputs, parses LP metadata and filesystems, simulates the new
/// layout with the same allocator lpmake uses and checks host disk space. Never writes anything.
/// </summary>
public static class StorageAnalyzer
{
    private const string Cap = "Capacity";
    private const string Inp = "Input";

    public static StorageReport Analyze(PreparationRequest req, IExt4Toolchain? toolchain, OperationLog? log = null)
    {
        log ??= new OperationLog();
        log.Info($"Analyzing {req.SourcePath} for profile '{req.Profile.Name}' (read-only).");

        if (!File.Exists(req.SourcePath))
        {
            var r0 = new StorageReport { Profile = req.Profile, SourcePath = req.SourcePath };
            r0.InputFindings.Add(new Finding(Severity.Fail, Inp, $"File not found: {req.SourcePath}"));
            return r0;
        }

        ImageFile file;
        try { file = ImageFile.Open(req.SourcePath); }
        catch (Exception ex)
        {
            var r0 = new StorageReport { Profile = req.Profile, SourcePath = req.SourcePath };
            r0.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Cannot open source: {ex.Message}"));
            return r0;
        }

        using (file)
        {
            string? archive = FileSystemDetector.ArchiveKind(file.Stream);
            bool isSuper = archive == null && LpMetadataReader.LooksLikeSuper(file.Stream);
            if (!isSuper) return AnalyzeNonSuper(req, file, archive, log);

            LpReadResult lp;
            try { lp = LpMetadataReader.Read(file.Stream); }
            catch (Exception ex)
            {
                var r0 = new StorageReport { Profile = req.Profile, SourcePath = req.SourcePath, SourceIsSuper = true, SourceIsSparse = file.IsSparse, SourceFileLength = file.FileLength, SourceRawLength = file.Length };
                r0.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Super metadata is invalid: {ex.Message}"));
                return r0;
            }
            var md = lp.Metadata;
            var dev = md.SuperDevice;
            var report = new StorageReport
            {
                Profile = req.Profile,
                SourcePath = req.SourcePath,
                SourceIsSuper = true,
                SourceIsSparse = file.IsSparse,
                SourceFileLength = file.FileLength,
                SourceRawLength = file.Length,
                SourceMetadata = md,
                DeviceSize = dev.Size,
                MetadataReservedBytes = dev.FirstLogicalSector * LpFormat.SectorSize,
                UsableBytes = md.UsableBytes,
                CurrentAllocatedBytes = md.AllocatedBytes,
                CurrentFreeBytes = dev.Size > md.HighestUsedByte ? dev.Size - md.HighestUsedByte : 0,
                ToolchainName = toolchain?.Name,
            };

            report.InputFindings.Add(new Finding(Severity.Pass, Inp, $"Valid super image: LP metadata v{md.MajorVersion}.{md.MinorVersion} with {md.Partitions.Count} partition(s), checksums verified."));
            foreach (var w in lp.Warnings) report.InputFindings.Add(new Finding(Severity.Warning, Inp, w));
            if (md.MinorVersion != LpFormat.MinorVersionAndroid10)
                report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"Metadata minor version {md.MinorVersion} is newer than Android 10's (0). The rebuilt super will use version 10.0, which Android 10 bootloaders/fs_mgr can read."));
            if (md.BlockDevices.Count != 1)
                report.InputFindings.Add(new Finding(Severity.Fail, Inp, "Retrofit / multi-device super layouts are not supported."));
            if (md.Extents.Any(e => e.TargetType != LpFormat.TargetLinear))
                report.InputFindings.Add(new Finding(Severity.Info, Inp, "Some extents are ZERO targets; they read as zeros."));
            if (md.Geometry.LogicalBlockSize != LpFormat.DefaultBlockSize)
                report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"Unusual logical block size {md.Geometry.LogicalBlockSize}."));

            // Current partitions + filesystem inspection
            var images = new Dictionary<string, AndroidImageInfo>();
            foreach (var p in md.Partitions)
            {
                AndroidImageInfo? info = null;
                try
                {
                    if (md.ExtentsOf(p).All(e => e.TargetSource == 0 || e.TargetType == LpFormat.TargetZero) && md.SizeOf(p) > 0)
                    {
                        var regions = md.ExtentsOf(p).Select(e => e.TargetType == LpFormat.TargetZero
                            ? (0L, (long)e.Bytes, true) : ((long)(e.TargetData * LpFormat.SectorSize), (long)e.Bytes, false));
                        using var ps = new ConcatRegionStream(file.Stream, regions);
                        info = AndroidImageInspector.Inspect(ps, p.Name, RoleOf(p.Name));
                        images[p.Name] = info;
                    }
                }
                catch (Exception ex)
                {
                    report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"Partition '{p.Name}' could not be inspected: {ex.Message}"));
                }
                report.SourcePartitions.Add(new SourcePartitionInfo
                {
                    Name = p.Name, Group = md.GroupNameOf(p), Attributes = p.Attributes, Size = md.SizeOf(p),
                    ExtentCount = (int)p.NumExtents, Image = info,
                });
                log.Info($"  partition {p.Name}: {ByteSize.WithExact(md.SizeOf(p))}, group {md.GroupNameOf(p)}{(info != null ? $", {StorageReport.Describe(info)}" : "")}");
            }

            // Profile expectations (from the original scripts: system, vendor, product)
            var names = md.Partitions.Select(p => p.Name).ToHashSet();
            if (req.Profile.Id == RomProfileId.LX04Chinese)
                report.InputFindings.Add(new Finding(Severity.Warning, Inp, "LX04 profile: the repository documents no LX04 procedure. This is an analysis only; building is disabled."));
            else
            {
                var expected = new[] { "system", "vendor", "product" };
                var missing = expected.Where(e => !names.Contains(e)).ToList();
                if (missing.Count > 0)
                    report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"The original scripts expect system, vendor and product; this super lacks: {string.Join(", ", missing)}. Partitions found: {string.Join(", ", names)}."));
                if (req.Profile.ReplacesSystem && !names.Contains("vendor"))
                    report.InputFindings.Add(new Finding(Severity.Fail, Inp, "The base super has no vendor partition, which a GSI needs."));
                if (images.TryGetValue("vendor", out var vi) && vi.VndkVersion != null)
                    report.InputFindings.Add(new Finding(Severity.Info, Inp, $"Vendor: Android {vi.Release ?? "?"} (SDK {vi.Sdk?.ToString() ?? "?"}), VNDK {vi.VndkVersion}, ABIs {vi.AbiList ?? "?"}."));
            }

            // Replacements
            var replacementInfo = new Dictionary<string, (ImageFileInfo File, AndroidImageInfo? Image, string? Archive)>();
            foreach (var (part, path) in req.Replacements)
            {
                if (!names.Contains(part))
                {
                    report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Cannot replace '{part}': no such partition in the source super."));
                    continue;
                }
                if (req.RemovePartitions.Contains(part))
                {
                    report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"'{part}' is selected for both replacement and removal."));
                    continue;
                }
                try
                {
                    using var rf = ImageFile.Open(path);
                    var arc = FileSystemDetector.ArchiveKind(rf.Stream);
                    AndroidImageInfo? ri = arc == null ? AndroidImageInspector.Inspect(rf.Stream, part, RoleOf(part)) : null;
                    replacementInfo[part] = (new ImageFileInfo(path, rf.IsSparse, rf.FileLength, rf.Length), ri, arc);
                    if (arc != null)
                        report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Replacement for '{part}' is a {arc} archive — extract the .img first."));
                    else if (LpMetadataReader.LooksLikeSuper(rf.Stream))
                        report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Replacement for '{part}' is itself a super image, not a partition image."));
                    else
                        report.InputFindings.Add(new Finding(Severity.Pass, Inp, $"Replacement for '{part}': {Path.GetFileName(path)} ({(rf.IsSparse ? "sparse" : "raw")}, {ByteSize.WithExact(rf.Length)}, {StorageReport.Describe(ri!)})."));
                }
                catch (Exception ex)
                {
                    report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"Replacement for '{part}' cannot be read: {ex.Message}"));
                }
            }
            foreach (var r in req.RemovePartitions)
                if (!names.Contains(r)) report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"'{r}' was selected for removal but does not exist in the source."));
            if (req.Profile.ReplacesSystem && !req.Replacements.ContainsKey("system"))
                report.InputFindings.Add(new Finding(Severity.Fail, Inp, "No system image selected."));

            // Compatibility of a replacement system
            if (replacementInfo.TryGetValue("system", out var sys) && req.Profile.Id != RomProfileId.LX04Chinese)
            {
                images.TryGetValue("vendor", out var vendorInfo);
                images.TryGetValue("system", out var stockSys);
                if (sys.Image != null || sys.Archive != null)
                    report.CompatibilityFindings.AddRange(SystemCompatibilityChecker.Check(req.Profile,
                        sys.Image ?? new AndroidImageInfo { Label = "system" }, vendorInfo, stockSys, sys.Archive));
            }
            else if (req.Profile.Id == RomProfileId.X04GGlobal)
            {
                if (images.TryGetValue("system", out var s0) && s0.Sdk is int sdk)
                    report.CompatibilityFindings.Add(new Finding(sdk == 29 ? Severity.Pass : Severity.Warning, "Compatibility",
                        $"Stock system in this super reports Android {s0.Release} (SDK {sdk}); the repository describes the X04G ROM as Android Go 10 (SDK 29)."));
                else
                    report.CompatibilityFindings.Add(new Finding(Severity.Warning, "Compatibility", "Could not read the Android version of the system partition in this super."));
            }
            else if (req.Profile.Id == RomProfileId.LX04Chinese)
                report.CompatibilityFindings.Add(new Finding(Severity.Warning, "Compatibility", "LX04 compatibility is not documented; nothing can be verified."));

            if (req.Profile.Id == RomProfileId.LX04Chinese)
            {
                report.CapacityFindings.Add(new Finding(Severity.Warning, Cap, "No target layout is defined for LX04, so no fit check is performed."));
                HostDisk(report, req, new List<PlannedPartition>(), md);
                return report;
            }

            // Plan the new layout
            foreach (var p in md.Partitions)
            {
                if (req.RemovePartitions.Contains(p.Name)) { report.Removed.Add(p.Name); continue; }
                if (replacementInfo.TryGetValue(p.Name, out var rep))
                {
                    if (rep.Archive != null) continue;
                    report.Planned.Add(new PlannedPartition
                    {
                        Name = p.Name, Group = md.GroupNameOf(p), Attributes = p.Attributes,
                        Source = rep.File.Path, IsReplacement = true, IsSparseFile = rep.File.IsSparse,
                        ImageLength = rep.File.RawLength,
                        AlignedSize = ByteSize.AlignUp((ulong)rep.File.RawLength, md.Geometry.LogicalBlockSize),
                        ShrinkLowerBound = ShrinkBound(rep.Image), Shrinkable = IsShrinkable(rep.Image), Image = rep.Image,
                    });
                }
                else if (!req.Replacements.ContainsKey(p.Name))
                {
                    images.TryGetValue(p.Name, out var ii);
                    // Use the filesystem length when known (lpunpack writes the whole partition; same thing for stock images).
                    report.Planned.Add(new PlannedPartition
                    {
                        Name = p.Name, Group = md.GroupNameOf(p), Attributes = p.Attributes,
                        Source = $"super:{p.Name}", ImageLength = (long)md.SizeOf(p), AlignedSize = md.SizeOf(p),
                        ShrinkLowerBound = ShrinkBound(ii), Shrinkable = IsShrinkable(ii), Image = ii,
                    });
                }
            }

            if (req.BuildSuper || req.Replacements.Count > 0 || req.RemovePartitions.Count > 0)
                PlanCapacity(report, req, md, toolchain);
            else
                report.CapacityFindings.Add(new Finding(Severity.Pass, Cap, $"The current layout uses {ByteSize.WithExact(md.AllocatedBytes)} of {ByteSize.WithExact(md.UsableBytes)} usable bytes."));

            HostDisk(report, req, report.Planned, md);
            log.Info($"Analysis result: capacity {report.CapacityVerdict}, compatibility {report.CompatibilityVerdict}.");
            return report;
        }
    }

    private sealed record ImageFileInfo(string Path, bool IsSparse, long FileLength, long RawLength);

    public static AndroidImageInspector.Role RoleOf(string partition)
    {
        var n = partition.EndsWith("_a") || partition.EndsWith("_b") ? partition[..^2] : partition;
        return n switch
        {
            "system" => AndroidImageInspector.Role.System,
            "vendor" => AndroidImageInspector.Role.Vendor,
            "product" => AndroidImageInspector.Role.Product,
            _ => AndroidImageInspector.Role.Other,
        };
    }

    private static bool IsShrinkable(AndroidImageInfo? i) => i is { FileSystem: FileSystemKind.Ext4, Ext4SharedBlocks: false, Ext4UsedBytes: not null };
    private static ulong? ShrinkBound(AndroidImageInfo? i) => IsShrinkable(i) ? i!.Ext4UsedBytes : null;

    /// <summary>Builds the layout request (shared with the executor so analysis and build use identical parameters).</summary>
    public static LpLayoutRequest LayoutRequestFor(LpMetadata src, IEnumerable<(PlannedPartition P, ulong Size)> parts, List<string>? notes = null)
    {
        var list = parts.ToList();
        var groups = new List<LpGroupRequest>();
        foreach (var g in src.Groups.Where(g => g.Name != "default"))
        {
            ulong need = list.Where(x => x.P.Group == g.Name).Aggregate(0UL, (a, x) => a + ByteSize.AlignUp(x.Size, src.Geometry.LogicalBlockSize));
            ulong max = g.MaximumSize;
            if (max != 0 && max < need)
            {
                notes?.Add($"Group '{g.Name}' maximum raised from {max:N0} to {need:N0} bytes (sum of its partitions), as the original repack scripts do.");
                max = need;
            }
            groups.Add(new LpGroupRequest(g.Name, max));
        }
        var dev = src.SuperDevice;
        return new LpLayoutRequest
        {
            DeviceSize = dev.Size,
            SuperName = dev.PartitionName,
            MetadataMaxSize = src.Geometry.MetadataMaxSize,
            MetadataSlotCount = src.Geometry.MetadataSlotCount,
            LogicalBlockSize = src.Geometry.LogicalBlockSize,
            Alignment = dev.Alignment,
            AlignmentOffset = dev.AlignmentOffset,
            Groups = groups,
            Partitions = list.Select(x => new LpPartitionRequest(x.P.Name, x.P.Group, x.P.Attributes, x.Size)).ToList(),
        };
    }

    private static void PlanCapacity(StorageReport report, PreparationRequest req, LpMetadata md, IExt4Toolchain? toolchain)
    {
        var notes = new List<string>();
        var layout = LpLayoutPlanner.Plan(LayoutRequestFor(md, report.Planned.Select(p => (p, (ulong)p.ImageLength)), notes));
        report.Layout = layout;
        foreach (var n in notes) report.CapacityFindings.Add(new Finding(Severity.Info, Cap, n));
        ulong usable = md.UsableBytes;

        if (layout.Success)
        {
            report.RequiredBytes = layout.EndOfAllocation - layout.FirstLogicalByte;
            report.RemainingBytes = layout.FreeAfterAllocation;
            report.AlignmentWasteBytes = layout.AlignmentWaste;
            report.CapacityFindings.Add(new Finding(Severity.Pass, Cap,
                $"The new layout fits: {ByteSize.WithExact(report.RequiredBytes.Value)} of {ByteSize.WithExact(usable)} usable, {ByteSize.Format(layout.FreeAfterAllocation)} left. Simulated with the lpmake allocator using the metadata parameters of your own super image."));
        }
        else
        {
            ulong need = report.Planned.Aggregate(0UL, (a, p) => a + p.AlignedSize);
            report.RequiredBytes = need;
            report.RemainingBytes = null;
            var sev = Severity.Fail;
            string extra = "";
            {
                // Lower bound if every shrinkable image were shrunk to its used blocks.
                var lbParts = report.Planned.Select(p => (p, p.Shrinkable && p.ShrinkLowerBound is ulong u ? Math.Min(u, (ulong)p.ImageLength) : (ulong)p.ImageLength)).ToList();
                var lbLayout = LpLayoutPlanner.Plan(LayoutRequestFor(md, lbParts));
                report.ShrinkLayout = lbLayout;
                report.ShrinkRequiredLowerBound = lbParts.Aggregate(0UL, (a, x) => a + ByteSize.AlignUp(x.Item2, md.Geometry.LogicalBlockSize));
                if (lbLayout.Success)
                {
                    sev = Severity.Warning;
                    extra = req.ShrinkExt4 && toolchain != null
                        ? " It may fit after shrinking with resize2fs -M (enabled). The real size is only known after shrinking; the build stops if it still does not fit."
                        : " It could only fit if the ext4 images were shrunk (resize2fs -M), which needs an ext4 toolchain (WSL or e2fsprogs) and the shrink option.";
                }
                else extra = " It does not fit even if every ext4 image were shrunk to its used data.";
            }
            report.CapacityFindings.Add(new Finding(sev, Cap, $"{layout.Error} Required {ByteSize.WithExact(need)}, usable {ByteSize.WithExact(usable)}.{extra}"));
        }

        if (req.ShrinkExt4)
        {
            if (toolchain == null) report.CapacityFindings.Add(new Finding(Severity.Warning, Cap, "Shrinking was requested but no ext4 toolchain (WSL with e2fsprogs, or tools\\e2fsprogs) is available. Images will be used at their current size."));
            foreach (var p in report.Planned.Where(p => !p.Shrinkable))
                report.CapacityFindings.Add(new Finding(Severity.Info, Cap, $"'{p.Name}' will not be shrunk ({(p.Image?.Ext4SharedBlocks == true ? "ext4 shared_blocks" : p.Image?.FileSystem.ToString() ?? "unknown filesystem")})."));
        }

        foreach (var p in report.Planned)
            if (p.Image?.Notes.FirstOrDefault(n => n.Contains("truncated")) is string t)
                report.CapacityFindings.Add(new Finding(Severity.Fail, Cap, $"'{p.Name}': {t}"));

        // Estimated output size
        ulong meta = (md.SuperDevice.FirstLogicalSector * LpFormat.SectorSize);
        if (req.Format == OutputFormat.Raw)
        {
            report.EstimatedOutputBytes = (long)md.SuperDevice.Size;
            report.EstimatedOutputIsUpperBound = false;
        }
        else
        {
            long data = report.Planned.Sum(p => p.ImageLength);
            report.EstimatedOutputBytes = (long)meta + data + 64 * 1024;
            report.EstimatedOutputIsUpperBound = true;
        }
    }

    private static void HostDisk(StorageReport report, PreparationRequest req, List<PlannedPartition> planned, LpMetadata? md)
    {
        var h = new HostDiskInfo { Directory = req.OutputDirectory, FreeBytes = SafeFile.AvailableFreeSpace(req.OutputDirectory) };
        if (req.ExtractPartitions && md != null)
            h.Breakdown.Add(("Extracted partition images", (long)md.AllocatedBytes));
        if (req.BuildSuper && md != null && req.Profile.Id != RomProfileId.LX04Chinese)
        {
            if (req.ShrinkExt4)
            {
                long copies = planned.Where(p => p.Shrinkable).Sum(p => p.ImageLength);
                if (copies > 0) h.Breakdown.Add(("Working copies for shrinking", copies));
            }
            h.Breakdown.Add((req.Format == OutputFormat.Raw ? "New super (raw)" : "New super (sparse, upper bound)", report.EstimatedOutputBytes ?? (long)md.SuperDevice.Size));
        }
        if (req.PatchBoot && req.BootImagePath != null && File.Exists(req.BootImagePath))
            h.Breakdown.Add(("Patched boot image", new FileInfo(req.BootImagePath).Length));
        h.Breakdown.Add(("Logs and report", 1 << 20));
        var hd = new HostDiskInfo { Directory = h.Directory, FreeBytes = h.FreeBytes, RequiredBytes = h.Breakdown.Sum(b => b.Bytes) };
        hd.Breakdown.AddRange(h.Breakdown);
        report.HostDisk = hd;

        if (hd.FreeBytes is long free)
        {
            if (free < hd.RequiredBytes)
                report.CapacityFindings.Add(new Finding(Severity.Fail, "Windows drive", $"Not enough free space on the output drive: {ByteSize.Format(free)} free, {ByteSize.Format(hd.RequiredBytes)} needed."));
            else if (free < hd.RequiredBytes + hd.RequiredBytes / 10 + (512L << 20))
                report.CapacityFindings.Add(new Finding(Severity.Warning, "Windows drive", $"Output drive is nearly full: {ByteSize.Format(free)} free, {ByteSize.Format(hd.RequiredBytes)} needed."));
            else
                report.CapacityFindings.Add(new Finding(Severity.Pass, "Windows drive", $"Output drive has {ByteSize.Format(free)} free; about {ByteSize.Format(hd.RequiredBytes)} needed."));
        }
        else report.CapacityFindings.Add(new Finding(Severity.Warning, "Windows drive", $"Free space on '{req.OutputDirectory}' could not be determined."));
    }

    private static StorageReport AnalyzeNonSuper(PreparationRequest req, ImageFile file, string? archive, OperationLog log)
    {
        AndroidImageInfo? info = null;
        string? inspectError = null;
        if (archive == null)
        {
            try { info = AndroidImageInspector.Inspect(file.Stream, Path.GetFileName(req.SourcePath), AndroidImageInspector.Role.System); }
            catch (Exception ex) { inspectError = ex.Message; }
        }
        var report = new StorageReport
        {
            Profile = req.Profile, SourcePath = req.SourcePath, SourceIsSuper = false, SourceIsSparse = file.IsSparse,
            SourceFileLength = file.FileLength, SourceRawLength = file.Length, SourceFilesystem = info,
        };
        if (archive != null)
        {
            report.InputFindings.Add(new Finding(Severity.Fail, Inp, $"The source is a {archive} archive. Extract it first."));
            return report;
        }
        if (inspectError != null) report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"Could not inspect the file: {inspectError}"));
        string desc = info != null ? StorageReport.Describe(info) : "unknown contents";
        if (req.Profile.Id == RomProfileId.LX04Chinese)
        {
            report.InputFindings.Add(new Finding(Severity.Warning, Inp, $"Not a super image ({desc}). That is expected for Android 8.1, which predates dynamic partitions; there is no super capacity to check."));
            report.CompatibilityFindings.Add(new Finding(Severity.Warning, "Compatibility", "LX04 compatibility is not documented; nothing can be verified."));
            report.CapacityFindings.Add(new Finding(Severity.Warning, Cap, "Capacity cannot be verified without logical partition metadata."));
        }
        else
        {
            report.InputFindings.Add(new Finding(Severity.Fail, Inp,
                $"'{Path.GetFileName(req.SourcePath)}' is not a super image (no logical partition metadata at offset 4096; contents: {desc}). Select super.bin from your MTKClient backup or a super.img."));
        }
        log.Info($"Source is not a super image ({info?.FileSystem.ToString() ?? "unknown"}).");
        return report;
    }
}
