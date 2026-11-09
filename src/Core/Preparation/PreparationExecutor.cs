using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Core.Preparation;

public sealed record OutputFile(string Path, long Size, string Description, string? Sha256);

public sealed class ExecutionResult
{
    public bool Success { get; set; }
    public bool DryRun { get; init; }
    public string? Error { get; set; }
    public string? RunDirectory { get; set; }
    public List<OutputFile> Outputs { get; } = new();
    public bool? Verified { get; set; }
    public LpMetadata? FinalMetadata { get; set; }
    public long? FinalSuperFileSize { get; set; }
    public long? DryRunSparseSize { get; set; }
    public string FinalReport { get; set; } = "";
}

/// <summary>Write-only seekable stream that discards data but tracks the resulting length (used for dry runs).</summary>
public sealed class CountingStream : Stream
{
    private long _len, _pos;
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _len;
    public override long Position { get => _pos; set => _pos = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _len + offset;
    public override void SetLength(long value) => _len = value;
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { _pos += buffer.Length; _len = Math.Max(_len, _pos); }
}

public sealed class PreparationExecutor
{
    private readonly OperationLog _log;
    private readonly IExt4Toolchain? _toolchain;
    private readonly IProgress<ProgressInfo>? _progress;

    public PreparationExecutor(OperationLog log, IExt4Toolchain? toolchain, IProgress<ProgressInfo>? progress = null)
    {
        _log = log; _toolchain = toolchain; _progress = progress;
    }

    public ExecutionResult Execute(PreparationPlan plan, CancellationToken ct = default)
    {
        var req = plan.Request;
        var result = new ExecutionResult { DryRun = req.DryRun };
        try
        {
            if (!plan.CanExecute) throw new ToolkitException("The plan cannot be executed: " + string.Join(" ", plan.Problems));

            // Re-analyze: inputs could have changed since the report was shown.
            _log.Info("Re-validating inputs before execution...");
            var report = StorageAnalyzer.Analyze(req, _toolchain, _log);
            if (report.BlocksExecution)
                throw new ToolkitException("Inputs no longer pass validation:\n" + string.Join("\n", report.InputFindings.Concat(report.CapacityFindings).Where(f => f.Severity == Severity.Fail).Select(f => f.Message)));

            if (req.DryRun) return DryRun(plan, report, result, ct);

            // Run folder
            string run = plan.RunDirectory;
            if (Directory.Exists(run) || File.Exists(run))
            {
                run = SafeFile.UniqueDirectory(req.OutputDirectory, Path.GetFileName(run));
                _log.Warn($"Planned run folder already exists; using {run} instead.");
            }
            Directory.CreateDirectory(run);
            result.RunDirectory = run;
            _log.AttachFile(Path.Combine(run, "operation.log"));
            _log.Info($"Run folder: {run}");
            _log.Info($"Profile: {req.Profile.Name}; source: {req.SourcePath}");

            using (var super = report.SourceIsSuper ? SuperImage.Open(req.SourcePath) : null)
            {
                if (req.ExtractPartitions && super != null) Extract(super, run, result, ct);
                if (req.BuildSuper && super != null) BuildSuper(super, report, req, run, result, ct);
            }

            if (req.PatchBoot && req.BootImagePath != null) PatchBoot(req.BootImagePath, run, result);

            // Final report & checksums
            var final = BuildFinalReport(report, result);
            result.FinalReport = final;
            var reportPath = SafeFile.UniquePath(run, "report.txt");
            using (var w = SafeFile.CreateNew(reportPath)) w.Write(Encoding.UTF8.GetBytes(final));
            var sums = new StringBuilder();
            foreach (var o in result.Outputs.Where(o => o.Sha256 != null))
                sums.AppendLine($"{o.Sha256}  {Path.GetRelativePath(run, o.Path).Replace('\\', '/')}");
            if (sums.Length > 0)
            {
                var sumPath = SafeFile.UniquePath(run, "SHA256SUMS.txt");
                using var w = SafeFile.CreateNew(sumPath);
                w.Write(Encoding.UTF8.GetBytes(sums.ToString()));
            }
            result.Outputs.Add(new OutputFile(reportPath, new FileInfo(reportPath).Length, "Final report", null));
            result.Success = true;
            _log.Info("Done. Nothing was flashed to the device.");
        }
        catch (OperationCanceledException)
        {
            result.Error = "Cancelled by the user.";
            _log.Warn("Cancelled. Files created so far are kept (incomplete outputs are renamed *.incomplete).");
        }
        catch (Exception ex)
        {
            result.Error = ex is ToolkitException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            _log.Error(result.Error);
            if (ex is not ToolkitException) _log.Debug(ex.ToString());
        }
        return result;
    }

    // ---------------------------------------------------------------- dry run
    private ExecutionResult DryRun(PreparationPlan plan, StorageReport report, ExecutionResult result, CancellationToken ct)
    {
        _log.Info("DRY RUN: no files or folders will be created.");
        foreach (var op in plan.Operations)
        {
            _log.Info($"[dry-run] {op.Description}");
            foreach (var c in op.Creates) _log.Info($"[dry-run]   would create {c}");
            foreach (var d in op.Deletes) _log.Info($"[dry-run]   would delete {d}");
        }
        var req = plan.Request;
        if (req.FilePatches.Count > 0)
            _log.Info($"[dry-run] File patches validated (read-only): {string.Join("; ", req.FilePatches)}. Partition sizes do not change.");
        if (req.BuildSuper && report.SourceIsSuper && report.Layout?.Success == true && !(req.ShrinkExt4 && _toolchain != null && report.Planned.Any(p => p.Shrinkable)))
        {
            _log.Info("[dry-run] Simulating the super build in memory (reads every input, writes nothing)...");
            using var super = SuperImage.Open(req.SourcePath);
            var contents = report.Planned.Select(p => DirectContent(super, p)).ToList();
            var cs = new CountingStream();
            IDeviceImageWriter w = req.Format == OutputFormat.Raw
                ? new RawImageWriter(cs, super.Metadata.Geometry.LogicalBlockSize, super.Metadata.SuperDevice.Size / super.Metadata.Geometry.LogicalBlockSize)
                : new SparseImageWriter(cs, super.Metadata.Geometry.LogicalBlockSize, super.Metadata.SuperDevice.Size / super.Metadata.Geometry.LogicalBlockSize);
            var r = SuperImageBuilder.Build(report.Layout.Metadata!, contents, w, _progress, ct);
            result.DryRunSparseSize = r.OutputFileSize;
            result.FinalMetadata = r.Metadata;
            _log.Info($"[dry-run] The new super would be {ByteSize.WithExact(r.OutputFileSize)}.");
            foreach (var (name, hash) in r.PartitionHashes) _log.Info($"[dry-run]   {name}: {r.PartitionLengths[name]:N0} bytes, sha256 {Convert.ToHexString(hash).ToLowerInvariant()}");
        }
        else if (req.BuildSuper)
            _log.Info("[dry-run] Final sizes depend on shrinking, which is not simulated in a dry run.");
        if (req.PatchBoot && req.BootImagePath != null)
        {
            using var bs = SafeFile.OpenReadOnly(req.BootImagePath);
            var info = BootImage.Read(bs);
            var n = BootImage.ComputeGsiCmdline(info.Cmdline, out _, out bool already);
            _log.Info($"[dry-run] boot cmdline: '{info.Cmdline}' → {(already ? "already permissive" : n == null ? "NOT patchable automatically" : $"'{n}'")}");
            if (n == null) throw new ToolkitException("The boot cmdline does not contain 'buildvariant=user'; the documented patch cannot be applied automatically.");
        }
        result.FinalReport = BuildFinalReport(report, result);
        result.Success = true;
        _log.Info("Dry run finished. Nothing was written.");
        return result;
    }

    // ---------------------------------------------------------------- extract
    private void Extract(SuperImage super, string run, ExecutionResult result, CancellationToken ct)
    {
        var dir = Path.Combine(run, "extracted");
        Directory.CreateDirectory(dir);
        foreach (var p in super.Metadata.Partitions)
        {
            if (super.Metadata.SizeOf(p) == 0) { _log.Info($"Skipping empty partition {p.Name}."); continue; }
            var path = SafeFile.UniquePath(dir, p.Name + ".img");
            _log.Info($"Extracting {p.Name} → {path}");
            var hash = WithIncomplete(path, () => super.ExtractPartition(p.Name, path, _progress, ct));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            result.Outputs.Add(new OutputFile(path, new FileInfo(path).Length, $"Extracted partition '{p.Name}'", hex));
            _log.Info($"  {ByteSize.WithExact(new FileInfo(path).Length)}, sha256 {hex}");
        }
    }

    private T WithIncomplete<T>(string path, Func<T> action)
    {
        try { return action(); }
        catch
        {
            try
            {
                if (File.Exists(path))
                {
                    var inc = SafeFile.UniquePath(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".incomplete");
                    File.Move(path, inc);
                    _log.Warn($"Incomplete output renamed to {inc}");
                }
            }
            catch (Exception ex) { _log.Warn($"Could not rename incomplete output {path}: {ex.Message}"); }
            throw;
        }
    }

    // ---------------------------------------------------------------- build
    private static PartitionContent DirectContent(SuperImage super, PlannedPartition p)
    {
        if (p.IsReplacement)
            return new PartitionContent
            {
                Name = p.Name, Length = p.ImageLength, SourceDescription = p.Source,
                Open = () => new OwningStream(ImageFile.Open(p.Source)),
            };
        return new PartitionContent { Name = p.Name, Length = p.ImageLength, SourceDescription = p.Source, Open = () => super.OpenPartition(p.Name) };
    }

    private void BuildSuper(SuperImage super, StorageReport report, PreparationRequest req, string run, ExecutionResult result, CancellationToken ct)
    {
        var md = super.Metadata;
        var contents = new List<(PlannedPartition P, PartitionContent C)>();
        bool shrink = req.ShrinkExt4 && _toolchain != null;
        if (req.ShrinkExt4 && _toolchain == null) _log.Warn("Shrinking requested, but no ext4 toolchain is available — images are used at their current size.");

        foreach (var p in report.Planned)
        {
            var baseContent = DirectContent(super, p);
            var patches = req.FilePatches.Where(f => f.Partition == p.Name).ToList();
            if (patches.Count > 0)
                baseContent = ApplyFilePatches(baseContent, p, patches, run, result, ct);

            if (shrink && p.Shrinkable)
            {
                var workDir = Path.Combine(run, "work");
                Directory.CreateDirectory(workDir);
                var work = SafeFile.UniquePath(workDir, p.Name + ".img");
                _log.Info($"Creating working copy of '{p.Name}' → {work}");
                WithIncomplete(work, () =>
                {
                    using var src = baseContent.Open();
                    using var dst = SafeFile.CreateNew(work);
                    return StreamCopy.CopyWithHash(src, dst, baseContent.Length, $"Copying {p.Name}", _progress, ct);
                });
                _progress?.Report(new ProgressInfo($"Shrinking {p.Name}", 0, "e2fsck / resize2fs"));
                _log.Info($"Shrinking '{p.Name}' with {_toolchain!.Name}...");
                Ext4Tools.ShrinkToMinimum(_toolchain, work, _log, ct);
                long len = new FileInfo(work).Length;
                _log.Info($"  '{p.Name}': {ByteSize.WithExact(p.ImageLength)} → {ByteSize.WithExact(len)}");
                result.Outputs.Add(new OutputFile(work, len, $"Shrunk working copy of '{p.Name}' (intermediate)", null));
                contents.Add((p, new PartitionContent { Name = p.Name, Length = len, SourceDescription = work, Open = () => SafeFile.OpenReadOnly(work) }));
            }
            else contents.Add((p, baseContent));
        }

        // Final layout from the real sizes
        var notes = new List<string>();
        var layout = LpLayoutPlanner.Plan(StorageAnalyzer.LayoutRequestFor(md, contents.Select(c => (c.P, (ulong)c.C.Length)), notes));
        foreach (var n in notes) _log.Info(n);
        if (!layout.Success)
            throw new ToolkitException($"FAIL: the new layout does not fit in the super partition: {layout.Error} No super image was written. Working copies (if any) are kept in the run folder.");
        _log.Info($"Layout: {ByteSize.WithExact(layout.EndOfAllocation - layout.FirstLogicalByte)} used of {ByteSize.WithExact(md.UsableBytes)}, {ByteSize.Format(layout.FreeAfterAllocation)} free.");
        foreach (var p in layout.Metadata!.Partitions)
        {
            var ex = layout.Metadata.ExtentsOf(p).ToList();
            _log.Info($"  {p.Name}: {ByteSize.WithExact(layout.Metadata.SizeOf(p))} at sector {(ex.Count > 0 ? ex[0].TargetData : 0)} ({ex.Count} extent(s))");
        }

        var outPath = SafeFile.UniquePath(run, PreparationPlanner.OutputFileName(req));
        _log.Info($"Writing {outPath} ({req.Format.ToString().ToLowerInvariant()})...");
        uint bs = md.Geometry.LogicalBlockSize;
        ulong blocks = md.SuperDevice.Size / bs;
        SuperBuildResult built = WithIncomplete(outPath, () =>
        {
            using var fs = SafeFile.CreateNew(outPath);
            IDeviceImageWriter w = req.Format == OutputFormat.Raw ? new RawImageWriter(fs, bs, blocks) : new SparseImageWriter(fs, bs, blocks);
            return SuperImageBuilder.Build(layout.Metadata, contents.Select(c => c.C).ToList(), w, _progress, ct);
        });
        result.FinalMetadata = built.Metadata;
        result.FinalSuperFileSize = built.OutputFileSize;
        _log.Info($"Wrote {ByteSize.WithExact(built.OutputFileSize)}.");
        foreach (var (name, h) in built.PartitionHashes)
            _log.Info($"  {name}: {built.PartitionLengths[name]:N0} bytes, sha256 {Convert.ToHexString(h).ToLowerInvariant()}");

        string? outHash = null;
        if (req.VerifyOutput)
        {
            Verify(outPath, built, ct);
            result.Verified = true;
        }
        outHash = Convert.ToHexString(HashFile(outPath, ct)).ToLowerInvariant();
        result.Outputs.Add(new OutputFile(outPath, new FileInfo(outPath).Length, $"New super image ({req.Format.ToString().ToLowerInvariant()})", outHash));

        if (req.CleanupIntermediates && req.FilePatches.Count == 0 && Directory.Exists(Path.Combine(run, "work")) && (result.Verified ?? false))
        {
            Directory.Delete(Path.Combine(run, "work"), true);
            result.Outputs.RemoveAll(o => o.Description.Contains("intermediate"));
            _log.Info("Deleted working copies (cleanup requested).");
        }
        else if (Directory.Exists(Path.Combine(run, "work")))
            _log.Info("Working copies are kept in the work folder.");
    }


    /// <summary>Materializes the partition as work/&lt;name&gt;.orig.img, then applies the in-place file patches into work/&lt;name&gt;.patched.img.</summary>
    private PartitionContent ApplyFilePatches(PartitionContent content, PlannedPartition p, List<FilePatch> patches, string run, ExecutionResult result, CancellationToken ct)
    {
        var workDir = Path.Combine(run, "work");
        Directory.CreateDirectory(workDir);
        var orig = SafeFile.UniquePath(workDir, p.Name + ".orig.img");
        _log.Info($"Writing unmodified '{p.Name}' to {orig} (reference copy for comparison and rollback)");
        var origHash = WithIncomplete(orig, () =>
        {
            using var src = content.Open();
            using var dst = SafeFile.CreateNew(orig);
            return StreamCopy.CopyWithHash(src, dst, content.Length, $"Copying {p.Name}", _progress, ct);
        });
        result.Outputs.Add(new OutputFile(orig, new FileInfo(orig).Length, $"Unmodified '{p.Name}' image (reference)", Convert.ToHexString(origHash).ToLowerInvariant()));
        string current = orig;
        for (int i = 0; i < patches.Count; i++)
        {
            var fp = patches[i];
            var next = SafeFile.UniquePath(workDir, patches.Count == 1 ? $"{p.Name}.patched.img" : $"{p.Name}.patched{i + 1}.img");
            var bytes = File.ReadAllBytes(fp.ReplacementFile);
            var r = WithIncomplete(next, () => Ext4FilePatcher.PatchCopy(current, next, fp.PathInImage, bytes, _log, _progress, ct));
            current = next;
        }
        var finalHash = Convert.ToHexString(HashFile(current, ct)).ToLowerInvariant();
        result.Outputs.Add(new OutputFile(current, new FileInfo(current).Length, $"Patched '{p.Name}' image ({string.Join("; ", patches.Select(x => x.PathInImage))})", finalHash));
        var path = current;
        return new PartitionContent { Name = p.Name, Length = new FileInfo(path).Length, SourceDescription = path, Open = () => SafeFile.OpenReadOnly(path) };
    }

    private byte[] HashFile(string path, CancellationToken ct)
    {
        using var fs = SafeFile.OpenReadOnly(path);
        return StreamCopy.Hash(fs, fs.Length, "Hashing output", _progress, ct);
    }

    private void Verify(string outPath, SuperBuildResult built, CancellationToken ct)
    {
        _log.Info("Verifying the new super image...");
        using var img = SuperImage.Open(outPath);
        foreach (var w in img.Warnings) throw new ToolkitException($"Verification failed: {w}");
        var expected = LpMetadataWriter.SerializeMetadata(built.Metadata);
        var actual = LpMetadataWriter.SerializeMetadata(img.Metadata);
        if (!expected.AsSpan().SequenceEqual(actual)) throw new ToolkitException("Verification failed: metadata read back differs from what was written.");
        if ((ulong)img.File.Length != built.Metadata.SuperDevice.Size) throw new ToolkitException("Verification failed: image size differs from the super size.");
        foreach (var (name, hash) in built.PartitionHashes)
        {
            using var ps = img.OpenPartition(name);
            var h = StreamCopy.Hash(ps, built.PartitionLengths[name], $"Verifying {name}", _progress, ct);
            if (!h.AsSpan().SequenceEqual(hash)) throw new ToolkitException($"Verification failed: contents of '{name}' differ.");
            _log.Info($"  {name}: OK");
        }
        _log.Info("Verification passed: metadata and all partition contents match.");
    }

    // ---------------------------------------------------------------- boot
    private void PatchBoot(string bootPath, string run, ExecutionResult result)
    {
        using (var bs = SafeFile.OpenReadOnly(bootPath))
        {
            var info = BootImage.Read(bs);
            _log.Info($"Boot image header v{info.HeaderVersion}, page size {info.PageSize}, cmdline '{info.Cmdline}'");
            var n = BootImage.ComputeGsiCmdline(info.Cmdline, out bool exact, out bool already);
            if (already) { _log.Warn("The boot cmdline already contains androidboot.selinux=permissive; no patched copy created."); return; }
            if (n == null) throw new ToolkitException("The boot cmdline does not contain 'buildvariant=user'; the documented patch cannot be applied automatically.");
            if (!exact) _log.Warn($"The cmdline differs from the documented original ('{BootImage.DocumentedOriginalCmdline}'); only the buildvariant token is changed.");
            var outPath = SafeFile.UniquePath(run, "boot_selinux_permissive.img");
            BootImage.WritePatchedCopy(bootPath, outPath, n);
            using var check = SafeFile.OpenReadOnly(outPath);
            var verify = BootImage.Read(check);
            if (verify.Cmdline != n) throw new ToolkitException("Boot patch verification failed.");
            var hash = Convert.ToHexString(StreamCopy.Hash(check, check.Length, "Hashing boot", null, default)).ToLowerInvariant();
            result.Outputs.Add(new OutputFile(outPath, check.Length, "Boot image with SELinux-permissive cmdline", hash));
            _log.Info($"Patched boot written: {outPath}; new cmdline '{n}'");
            _log.Warn("The patched boot no longer matches its AVB hash. As documented, flash vbmeta with --disable-verity --disable-verification (empty vbmeta from MTKClient).");
        }
    }

    // ---------------------------------------------------------------- report
    private static string BuildFinalReport(StorageReport report, ExecutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.DryRun ? "DRY RUN — nothing was written." : "RESULT");
        if (result.RunDirectory != null) sb.AppendLine($"Run folder: {result.RunDirectory}");
        if (result.FinalMetadata is { } md)
        {
            ulong end = md.HighestUsedByte;
            sb.AppendLine();
            sb.AppendLine("Final super layout");
            sb.AppendLine($"  Super size: {ByteSize.WithExact(md.SuperDevice.Size)}");
            sb.AppendLine($"  Used up to: {ByteSize.WithExact(end)}; free after layout: {ByteSize.WithExact(md.SuperDevice.Size - end)}");
            foreach (var p in md.Partitions)
                sb.AppendLine($"  {p.Name,-12} {ByteSize.WithExact(md.SizeOf(p))}  group {md.GroupNameOf(p)}");
            foreach (var g in md.Groups.Where(g => g.Name != "default"))
                sb.AppendLine($"  group {g.Name}: max {(g.MaximumSize == 0 ? "unlimited" : ByteSize.WithExact(g.MaximumSize))}");
        }
        if (result.FinalSuperFileSize is long fs) sb.AppendLine($"  Output file: {ByteSize.WithExact(fs)}");
        if (result.DryRunSparseSize is long ds) sb.AppendLine($"  Output file (dry run, exact): {ByteSize.WithExact(ds)}");
        if (result.Verified == true) sb.AppendLine("  Verification: PASSED (metadata and SHA-256 of every partition re-read from the output)");
        sb.AppendLine();
        if (result.Outputs.Count > 0)
        {
            sb.AppendLine("Files created");
            foreach (var o in result.Outputs) sb.AppendLine($"  {o.Path}  {ByteSize.Format(o.Size)}  {o.Description}");
            sb.AppendLine();
        }
        sb.AppendLine("Compatibility (unchanged by building):");
        foreach (var f in report.CompatibilityFindings) sb.AppendLine($"  [{f.Severity.ToString().ToUpperInvariant(),-7}] {f.Message}");
        sb.AppendLine();
        sb.AppendLine("A successful build only proves the image is structurally valid. It does not prove it boots on the device. Nothing was flashed.");
        sb.AppendLine();
        sb.AppendLine("---- Pre-build analysis ----");
        sb.Append(report.ToText());
        return sb.ToString();
    }
}

/// <summary>Wraps an <see cref="ImageFile"/> stream and disposes the file with it.</summary>
internal sealed class OwningStream : Stream
{
    private readonly ImageFile _f;
    public OwningStream(ImageFile f) => _f = f;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _f.Stream.Length;
    public override long Position { get => _f.Stream.Position; set => _f.Stream.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _f.Stream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _f.Stream.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => _f.Stream.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _f.Dispose(); base.Dispose(disposing); }
}
