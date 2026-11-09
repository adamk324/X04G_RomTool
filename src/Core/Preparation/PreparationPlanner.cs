using X04G.Core.Common;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Core.Preparation;

public enum OperationKind { CreateFolder, Extract, WorkingCopy, Shrink, BuildSuper, Verify, PatchBoot, WriteReport, Cleanup }

public sealed record PlannedOperation(OperationKind Kind, string Description, IReadOnlyList<string> Reads, IReadOnlyList<string> Creates, IReadOnlyList<string> Deletes);

public sealed class PreparationPlan
{
    public required PreparationRequest Request { get; init; }
    public required StorageReport Report { get; init; }
    public required string RunDirectory { get; init; }
    public List<PlannedOperation> Operations { get; } = new();
    public List<string> Problems { get; } = new();
    public bool CanExecute => Problems.Count == 0;

    public IEnumerable<string> AllReads => Operations.SelectMany(o => o.Reads).Distinct();
    public IEnumerable<string> AllCreates => Operations.SelectMany(o => o.Creates).Distinct();

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Request.DryRun ? "DRY RUN — nothing will be written." : "The following will be done:");
        sb.AppendLine($"Run folder (new): {RunDirectory}");
        sb.AppendLine();
        int i = 1;
        foreach (var op in Operations)
        {
            sb.AppendLine($"{i++}. {op.Description}");
            foreach (var r in op.Reads) sb.AppendLine($"     reads (read-only): {r}");
            foreach (var c in op.Creates) sb.AppendLine($"     creates:           {c}");
            foreach (var d in op.Deletes) sb.AppendLine($"     deletes:           {d}");
        }
        sb.AppendLine();
        sb.AppendLine("Original files are only opened read-only. Nothing existing is overwritten. Nothing is flashed to the device.");
        if (Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Cannot run:");
            foreach (var p in Problems) sb.AppendLine($"  - {p}");
        }
        return sb.ToString();
    }
}

public static class PreparationPlanner
{
    public static string OutputFileName(PreparationRequest r) =>
        $"super_{ProfileTag(r.Profile)}_new{(r.Format == OutputFormat.Raw ? ".raw" : "")}.img";

    public static string ProfileTag(RomProfile p) => p.Id switch
    {
        RomProfileId.X04GGlobal => "X04G",
        RomProfileId.LX04Chinese => "LX04",
        RomProfileId.Gsi => "GSI",
        _ => "custom",
    };

    public static PreparationPlan Plan(PreparationRequest req, StorageReport report, bool toolchainAvailable, DateTime? now = null)
    {
        var run = SafeFile.UniqueDirectory(req.OutputDirectory, $"X04G_{ProfileTag(req.Profile)}", now);
        var plan = new PreparationPlan { Request = req, Report = report, RunDirectory = run };
        var ops = plan.Operations;
        string In(string f) => Path.Combine(run, f);

        if (report.BlocksExecution)
            plan.Problems.Add("The compatibility report contains a FAIL in the input or capacity checks.");
        if (req.Profile.Id == RomProfileId.LX04Chinese && (req.BuildSuper || req.ExtractPartitions && !report.SourceIsSuper))
            plan.Problems.Add("LX04 is analysis-only; only extraction from an LX04 super image is possible.");
        if (!req.BuildSuper && !req.ExtractPartitions && !req.PatchBoot)
            plan.Problems.Add("No operation selected.");
        if (req.ExtractPartitions && !report.SourceIsSuper)
            plan.Problems.Add("Extraction needs a super image.");
        if (req.PatchBoot && (req.BootImagePath == null || !File.Exists(req.BootImagePath)))
            plan.Problems.Add("Boot patch selected but no boot image was chosen.");

        ops.Add(new PlannedOperation(OperationKind.CreateFolder, "Create a new run folder", Array.Empty<string>(), new[] { run }, Array.Empty<string>()));

        if (req.ExtractPartitions && report.SourceMetadata is { } md)
            ops.Add(new PlannedOperation(OperationKind.Extract, $"Extract {md.Partitions.Count} partition(s) from the source super (lpunpack equivalent)",
                new[] { req.SourcePath }, md.Partitions.Select(p => In(Path.Combine("extracted", p.Name + ".img"))).ToList(), Array.Empty<string>()));

        if (req.BuildSuper && report.SourceIsSuper)
        {
            foreach (var grp in req.FilePatches.GroupBy(f => f.Partition))
                ops.Add(new PlannedOperation(OperationKind.WorkingCopy,
                    $"Copy '{grp.Key}' and replace in place: {string.Join("; ", grp.Select(f => f.PathInImage))} (same inode, blocks and SELinux label; verified block-by-block)",
                    grp.Select(f => f.ReplacementFile).Prepend(req.SourcePath).Distinct().ToList(),
                    new[] { In(Path.Combine("work", grp.Key + ".orig.img")), In(Path.Combine("work", grp.Key + ".patched.img")) }, Array.Empty<string>()));
            var shrink = req.ShrinkExt4 && toolchainAvailable ? report.Planned.Where(p => p.Shrinkable).ToList() : new();
            foreach (var p in shrink)
            {
                var work = In(Path.Combine("work", p.Name + ".img"));
                ops.Add(new PlannedOperation(OperationKind.WorkingCopy, $"Copy '{p.Name}' to a working image", new[] { p.IsReplacement ? p.Source : req.SourcePath }, new[] { work }, Array.Empty<string>()));
                ops.Add(new PlannedOperation(OperationKind.Shrink, $"Shrink the working copy of '{p.Name}' (e2fsck -fy; resize2fs -M; e2fsck -fy)", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
            }
            var reads = new List<string> { req.SourcePath };
            reads.AddRange(report.Planned.Where(p => p.IsReplacement).Select(p => p.Source));
            var desc = $"Build new super ({req.Format.ToString().ToLowerInvariant()}): {string.Join(", ", report.Planned.Select(p => p.Name + (p.IsReplacement ? " (replaced)" : "")))}" +
                       (report.Removed.Count > 0 ? $"; removed: {string.Join(", ", report.Removed)}" : "");
            ops.Add(new PlannedOperation(OperationKind.BuildSuper, desc, reads.Distinct().ToList(), new[] { In(OutputFileName(req)) }, Array.Empty<string>()));
            if (req.VerifyOutput)
                ops.Add(new PlannedOperation(OperationKind.Verify, "Re-open the new super and verify metadata and SHA-256 of every partition", new[] { In(OutputFileName(req)) }, Array.Empty<string>(), Array.Empty<string>()));
            if (req.CleanupIntermediates && shrink.Count > 0 && req.FilePatches.Count == 0)
                ops.Add(new PlannedOperation(OperationKind.Cleanup, "Delete the working copies after successful verification", Array.Empty<string>(), Array.Empty<string>(), new[] { In("work") }));
        }

        if (req.PatchBoot && req.BootImagePath != null)
            ops.Add(new PlannedOperation(OperationKind.PatchBoot, "Copy the boot image with the documented SELinux-permissive cmdline (WorkingWithGSI.md)",
                new[] { req.BootImagePath }, new[] { In("boot_selinux_permissive.img") }, Array.Empty<string>()));

        ops.Add(new PlannedOperation(OperationKind.WriteReport, "Write the operation log, final report and SHA-256 checksums", Array.Empty<string>(),
            new[] { In("operation.log"), In("report.txt"), In("SHA256SUMS.txt") }, Array.Empty<string>()));
        return plan;
    }
}
