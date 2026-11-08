using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Core.Tests;

public static class AnalysisTests
{
    private static PreparationRequest Req(RomProfile p, string src, string? system = null, bool build = true, string? outDir = null)
    {
        var r = new PreparationRequest { Profile = p, SourcePath = src, OutputDirectory = outDir ?? Fixtures.NewDir("out"), BuildSuper = build };
        if (system != null) r.Replacements["system"] = system;
        if (p.RemovesProductByDefault) r.RemovePartitions.Add("product");
        return r;
    }

    [Test]
    public static void StockRepack_Passes()
    {
        var rep = StorageAnalyzer.Analyze(Req(RomProfileCatalog.X04G, Fixtures.StockSuper()), null);
        Assert.Equal(Verdict.Pass, rep.CapacityVerdict, rep.ToText());
        Assert.Equal(16UL << 20, rep.DeviceSize!.Value);
        Assert.Equal(1UL << 20, rep.MetadataReservedBytes!.Value);
        Assert.Equal(7UL << 20, rep.RequiredBytes!.Value);
        Assert.Equal(8UL << 20, rep.RemainingBytes!.Value); // 16 MiB - 1 MiB metadata area - 7 MiB
        Assert.Equal(3, rep.SourcePartitions.Count);
        Assert.Equal(29, rep.SourcePartitions.First(p => p.Name == "vendor").Image!.Sdk!.Value);
    }

    [Test]
    public static void Gsi_FitsAfterRemovingProduct_ButCompatibilityIsNeverPass()
    {
        var rep = StorageAnalyzer.Analyze(Req(RomProfileCatalog.Gsi, Fixtures.StockSuper(), Fixtures.Ext4("gsi_sar_arm64_sdk30.img")), null);
        Assert.Equal(Verdict.Pass, rep.CapacityVerdict, rep.ToText());
        Assert.True(rep.Removed.Contains("product"));
        Assert.Equal(Verdict.Warning, rep.CompatibilityVerdict, "a GSI is never reported as fully compatible");
        var text = string.Join("\n", rep.CompatibilityFindings);
        Assert.Contains("VNDK v29", text);          // ships com.android.vndk.v29 apex
        Assert.Contains("system-as-root", text);    // layout matches stock
        Assert.Contains("32-bit-only", text);       // arm64 GSI vs 32-bit vendor ABI list
        Assert.Contains("SELinux", text);
    }

    [Test]
    public static void Gsi_InsufficientStorage_Fails()
    {
        // 8 MiB super: stock 4+2+1 MiB fits exactly; a 6 MiB GSI + 2 MiB vendor does not fit in 7 MiB usable.
        var req = Req(RomProfileCatalog.Gsi, Fixtures.StockSuper(8 << 20), Fixtures.Ext4("gsi_sar_arm64_sdk30.img"));
        var rep = StorageAnalyzer.Analyze(req, null);
        Assert.True(rep.CapacityVerdict != Verdict.Pass, rep.ToText());
        Assert.True(rep.CapacityFindings.Any(f => f.Message.Contains("shrunk")), "mentions shrinking");
        // Without shrinking possible (lower bound fits) this is a WARNING, never PASS
        Assert.Equal(Verdict.Warning, rep.CapacityVerdict);
        var plan = PreparationPlanner.Plan(req, rep, false);
        // Execution without shrinking must stop before writing a super image
        var log = new OperationLog();
        var res = new PreparationExecutor(log, null).Execute(plan);
        Assert.False(res.Success);
        Assert.Contains("does not fit", res.Error!);
        Assert.False(Directory.Exists(plan.RunDirectory) && Directory.GetFiles(plan.RunDirectory, "super_*.img").Length > 0, "no super written");
    }

    [Test]
    public static void KeepingProduct_InTightSuper_Fails()
    {
        var r = Req(RomProfileCatalog.Gsi, Fixtures.StockSuper(8 << 20), Fixtures.Ext4("gsi_sar_arm64_sdk30.img"));
        r.RemovePartitions.Clear(); // GSI (6 MiB) + vendor (2 MiB) + product (1 MiB) > 7 MiB usable
        var rep = StorageAnalyzer.Analyze(r, null);
        Assert.True(rep.CapacityVerdict != Verdict.Pass);
    }

    [Test]
    public static void OldNonSarGsi_FailsCompatibility()
    {
        var rep = StorageAnalyzer.Analyze(Req(RomProfileCatalog.Gsi, Fixtures.StockSuper(), Fixtures.Ext4("gsi_nonsar_sdk28.img")), null);
        Assert.Equal(Verdict.Fail, rep.CompatibilityVerdict, rep.ToText());
        var text = string.Join("\n", rep.CompatibilityFindings);
        Assert.Contains("older", text);
        Assert.Contains("layout", text);
    }

    [Test]
    public static void InvalidInputs_AreReportedAsFail()
    {
        // Not a super image
        var rep = StorageAnalyzer.Analyze(Req(RomProfileCatalog.X04G, Fixtures.Ext4("vendor_stock.img")), null);
        Assert.Equal(Verdict.Fail, rep.CapacityVerdict);
        Assert.Contains("not a super image", rep.InputFindings[0].Message);
        // Missing file
        var rep2 = StorageAnalyzer.Analyze(Req(RomProfileCatalog.X04G, "/nonexistent/super.bin"), null);
        Assert.True(rep2.BlocksExecution);
        // Compressed GSI
        var gz = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsi_sar_arm64_sdk30.img.gz");
        var rep3 = StorageAnalyzer.Analyze(Req(RomProfileCatalog.Gsi, Fixtures.StockSuper(), gz), null);
        Assert.True(rep3.BlocksExecution, rep3.ToText());
        Assert.True(rep3.InputFindings.Any(f => f.Message.Contains("gzip")));
        // GSI without system image
        var rep4 = StorageAnalyzer.Analyze(Req(RomProfileCatalog.Gsi, Fixtures.StockSuper()), null);
        Assert.True(rep4.BlocksExecution);
        // Super without vendor
        var noVendorDir = Fixtures.NewDir();
        var sys = Fixtures.Ext4("system_stock.img");
        var layout = LpLayoutPlanner.Plan(new LpLayoutRequest { DeviceSize = 8 << 20, Groups = { new("main", 0) }, Partitions = { new("system", "main", 1, (ulong)new FileInfo(sys).Length) } });
        var nv = Path.Combine(noVendorDir, "nv.bin");
        using (var fs = File.Create(nv))
            SuperImageBuilder.Build(layout.Metadata!, new[] { new PartitionContent { Name = "system", Length = new FileInfo(sys).Length, Open = () => File.OpenRead(sys) } }, new RawImageWriter(fs, 4096, 2048));
        var rep5 = StorageAnalyzer.Analyze(Req(RomProfileCatalog.Gsi, nv, Fixtures.Ext4("gsi_sar_arm64_sdk30.img")), null);
        Assert.True(rep5.InputFindings.Any(f => f.Severity == Severity.Fail && f.Message.Contains("vendor")), rep5.ToText());
    }

    [Test]
    public static void TruncatedSuper_IsNotPass()
    {
        var path = Fixtures.StockSuper();
        var bytes = File.ReadAllBytes(path);
        var t = Path.Combine(Fixtures.NewDir(), "trunc.bin");
        File.WriteAllBytes(t, bytes[..(4 << 20)]);
        var rep = StorageAnalyzer.Analyze(Req(RomProfileCatalog.X04G, t), null);
        Assert.True(rep.CapacityVerdict != Verdict.Pass, rep.ToText());
        Assert.True(rep.InputFindings.Any(f => f.Message.Contains("differs from the super size")));
    }

    [Test]
    public static void Lx04_IsAnalysisOnly()
    {
        var r = Req(RomProfileCatalog.LX04, Fixtures.Ext4("gsi_nonsar_sdk28.img"), build: false);
        var rep = StorageAnalyzer.Analyze(r, null);
        Assert.Equal(Verdict.Warning, rep.Overall);
        r.BuildSuper = true;
        var plan = PreparationPlanner.Plan(r, rep, false);
        Assert.False(plan.CanExecute);
    }

    [Test]
    public static void HostDisk_FreeSpaceIsReported()
    {
        var r = Req(RomProfileCatalog.X04G, Fixtures.StockSuper());
        var rep = StorageAnalyzer.Analyze(r, null);
        Assert.True(rep.HostDisk!.FreeBytes != null, "free space should be determinable for temp dir");
        Assert.True(rep.HostDisk.RequiredBytes > 0);
    }
}

public static class ExecutionTests
{
    private static (PreparationRequest, StorageReport, PreparationPlan) Setup(Action<PreparationRequest>? tweak = null, RomProfile? profile = null, ulong size = 16 << 20, IExt4Toolchain? tc = null)
    {
        profile ??= RomProfileCatalog.Gsi;
        var req = new PreparationRequest { Profile = profile, SourcePath = Fixtures.StockSuper(size), OutputDirectory = Fixtures.NewDir("out"), BuildSuper = true };
        if (profile.ReplacesSystem) req.Replacements["system"] = Fixtures.Ext4("gsi_sar_arm64_sdk30.img");
        if (profile.RemovesProductByDefault) req.RemovePartitions.Add("product");
        tweak?.Invoke(req);
        var rep = StorageAnalyzer.Analyze(req, tc);
        return (req, rep, PreparationPlanner.Plan(req, rep, tc != null));
    }

    [Test]
    public static void GsiBuild_VerifiesAndLeavesOriginalsUntouched()
    {
        var (req, rep, plan) = Setup(r => { r.ExtractPartitions = true; r.BootImagePath = Fixtures.BootImage(BootImage.DocumentedOriginalCmdline); r.PatchBoot = true; });
        var before = new[] { req.SourcePath, req.Replacements["system"], req.BootImagePath! }.ToDictionary(p => p, p => Convert.ToHexString(Fixtures.Sha(p)));
        var res = new PreparationExecutor(new OperationLog(), null).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        Assert.Equal(true, res.Verified);
        foreach (var (p, h) in before) Assert.Equal(h, Convert.ToHexString(Fixtures.Sha(p)), $"original modified: {p}");

        var superOut = res.Outputs.Single(o => o.Description.StartsWith("New super")).Path;
        using var img = SuperImage.Open(superOut);
        Assert.True(img.File.IsSparse);
        Assert.Equal("system,vendor", string.Join(",", img.Metadata.Partitions.Select(p => p.Name)));
        var gsi = req.Replacements["system"];
        using (var ps = img.OpenPartition("system"))
            Assert.SeqEqual(Fixtures.Sha(gsi), Fixtures.Sha(ps, new FileInfo(gsi).Length), "GSI content");
        Assert.True(File.Exists(Path.Combine(res.RunDirectory!, "extracted", "product.img")));
        Assert.True(File.Exists(Path.Combine(res.RunDirectory!, "boot_selinux_permissive.img")));
        Assert.True(File.Exists(Path.Combine(res.RunDirectory!, "operation.log")));
        Assert.True(File.ReadAllText(Path.Combine(res.RunDirectory!, "SHA256SUMS.txt")).Contains("super_GSI_new.img"));
        Assert.Contains("does not prove it boots", res.FinalReport);
    }

    [Test]
    public static void SecondRun_NeverOverwrites()
    {
        var (req, rep, plan) = Setup(profile: RomProfileCatalog.X04G);
        var r1 = new PreparationExecutor(new OperationLog(), null).Execute(plan);
        Assert.True(r1.Success, r1.Error ?? "");
        // Same plan again (same run folder name) → a different folder is used
        var r2 = new PreparationExecutor(new OperationLog(), null).Execute(plan);
        Assert.True(r2.Success, r2.Error ?? "");
        Assert.True(r1.RunDirectory != r2.RunDirectory, "second run must use a new folder");
        var existing = Path.Combine(r1.RunDirectory!, "super_X04G_new.img");
        Assert.Throws<OutputExistsException>(() => SafeFile.CreateNew(existing).Dispose());
        Assert.Equal(Path.Combine(r1.RunDirectory!, "super_X04G_new_1.img"), SafeFile.UniquePath(r1.RunDirectory!, "super_X04G_new.img"));
    }

    [Test]
    public static void StockRepack_ReproducesIdenticalSuper()
    {
        // Repacking an unmodified super with the same parameters must reproduce the same expanded image.
        var (req, rep, plan) = Setup(r => r.Format = OutputFormat.Raw, RomProfileCatalog.X04G);
        var res = new PreparationExecutor(new OperationLog(), null).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        var outPath = res.Outputs.Single(o => o.Description.StartsWith("New super")).Path;
        Assert.SeqEqual(Fixtures.Sha(req.SourcePath), Fixtures.Sha(outPath), "byte-identical repack");
    }

    [Test]
    public static void DryRun_WritesNothing()
    {
        var (req, rep, plan) = Setup(r => { r.DryRun = true; r.ExtractPartitions = true; });
        var log = new OperationLog();
        var res = new PreparationExecutor(log, null).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        Assert.Equal(0, Directory.GetFileSystemEntries(req.OutputDirectory).Length, "output folder must stay empty");
        Assert.True(res.DryRunSparseSize > 0);
        Assert.True(log.Entries.Any(e => e.Message.Contains("would create")));
    }

    [Test]
    public static void MissingToolchain_IsHandledGracefully()
    {
        var bogus = new NativeExt4Toolchain(Fixtures.NewDir("notools"));
        Assert.NotNull(bogus.Probe(), "probe must explain what is missing");
        var (req, rep, plan) = Setup(r => r.ShrinkExt4 = true);
        Assert.True(rep.CapacityFindings.Any(f => f.Message.Contains("no ext4 toolchain")), rep.ToText());
        var log = new OperationLog();
        var res = new PreparationExecutor(log, null).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        Assert.True(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("toolchain")));
    }

    [Test]
    public static void Shrink_WithE2fsprogs_MakesTightGsiFit()
    {
        if (Fixtures.FindTool("resize2fs") is not string r2) throw new SkipException("e2fsprogs not installed");
        var tc = new NativeExt4Toolchain(Path.GetDirectoryName(r2)!);
        if (tc.Probe() is string why) throw new SkipException(why);
        var (req, rep, plan) = Setup(r => { r.ShrinkExt4 = true; r.CleanupIntermediates = false; }, size: 8 << 20, tc: tc);
        Assert.Equal(Verdict.Warning, rep.CapacityVerdict, rep.ToText());
        var gsiHash = Convert.ToHexString(Fixtures.Sha(req.Replacements["system"]));
        var res = new PreparationExecutor(new OperationLog(), tc).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        Assert.Equal(gsiHash, Convert.ToHexString(Fixtures.Sha(req.Replacements["system"])), "original GSI untouched");
        var work = Path.Combine(res.RunDirectory!, "work", "system.img");
        Assert.True(File.Exists(work), "working copy kept");
        Assert.True(new FileInfo(work).Length < new FileInfo(req.Replacements["system"]).Length);
        // The shrunk filesystem is still readable and has its build.prop
        using var fs = File.OpenRead(work);
        Assert.Contains("ro.system.build.version.sdk=30", System.Text.Encoding.UTF8.GetString(new Ext4Image(fs).ReadFile("/system/build.prop")!));
    }

    [Test]
    public static void CancelledBuild_LeavesIncompleteMarker()
    {
        var (req, rep, plan) = Setup(profile: RomProfileCatalog.X04G);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(_ => { });
        var log = new OperationLog();
        cts.Cancel();
        var res = new PreparationExecutor(log, null).Execute(plan, cts.Token);
        Assert.False(res.Success);
        Assert.Contains("Cancelled", res.Error!);
        if (res.RunDirectory != null)
            Assert.Equal(0, Directory.GetFiles(res.RunDirectory, "super_*.img").Length, "no complete-looking super after cancel");
    }
}
