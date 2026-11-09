using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

// Headless front-end over X04G.Core, mainly for scripting and automated tests.
// The Windows desktop application is the primary interface.
static int Usage()
{
    Console.WriteLine("""
    x04g-cli — X04G Windows ROM Toolkit (command line)

      x04g-cli info <file>
      x04g-cli analyze --profile x04g|lx04|gsi|custom --source <super> [options]
      x04g-cli build   --profile x04g|gsi|custom --source <super> --out <dir> [options]
      x04g-cli unpack  --source <super> --out <dir>
      x04g-cli patch-file --image <ext4 img> --path </etc/file> --with <new file> --output <new img>
      x04g-cli lpmake  --device-size <bytes> --output <new file> [--sparse] name=image ...
                       (same fixed parameters as the original scripts: 64 KiB metadata, 1 slot, group "main")

    options:
      --system <img>        replacement system image (required for gsi/custom)
      --replace <part>=<img> replace any partition
      --patch <part>:<path>=<file>  replace one file inside a partition, in place (e.g. vendor:/etc/x.xml=x.xml)
      --remove <part>       leave a partition out (gsi removes product by default)
      --keep-product        gsi: keep product
      --shrink              shrink ext4 images with e2fsck/resize2fs (on copies)
      --raw                 write a raw super instead of sparse
      --extract             also extract all source partitions
      --boot <img> --patch-boot   SELinux-permissive boot cmdline (documented GSI step)
      --cleanup             delete working copies after a verified build
      --no-verify           skip read-back verification
      --dry-run             validate and simulate; write nothing
      --tools <dir>         directory containing e2fsck and resize2fs
    """);
    return 2;
}

if (args.Length == 0) return Usage();
var cmd = args[0];
var opts = new Dictionary<string, List<string>>();
var flags = new HashSet<string>();
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    var a = args[i];
    if (a.StartsWith("--"))
    {
        var k = a[2..];
        if (k is "sparse" or "keep-product" or "shrink" or "raw" or "extract" or "patch-boot" or "cleanup" or "no-verify" or "dry-run") flags.Add(k);
        else if (i + 1 < args.Length) { if (!opts.TryGetValue(k, out var l)) opts[k] = l = new(); l.Add(args[++i]); }
        else { Console.Error.WriteLine($"Missing value for {a}"); return 2; }
    }
    else positional.Add(a);
}
string? Opt(string k) => opts.TryGetValue(k, out var l) ? l[^1] : null;

try
{
    if (cmd == "info")
    {
        if (positional.Count != 1) return Usage();
        using var f = ImageFile.Open(positional[0]);
        Console.WriteLine($"{positional[0]}: {(f.IsSparse ? "sparse" : "raw")} {f.FileLength:N0} bytes, expanded {f.Length:N0}");
        if (LpMetadataReader.LooksLikeSuper(f.Stream))
        {
            var r = LpMetadataReader.Read(f.Stream);
            var md = r.Metadata;
            var d = md.SuperDevice;
            Console.WriteLine($"LP metadata v{md.MajorVersion}.{md.MinorVersion}; slots {md.Geometry.MetadataSlotCount}; max {md.Geometry.MetadataMaxSize}; block {md.Geometry.LogicalBlockSize}");
            Console.WriteLine($"Block device '{d.PartitionName}': size {d.Size:N0}, first sector {d.FirstLogicalSector}, alignment {d.Alignment}+{d.AlignmentOffset}");
            foreach (var g in md.Groups) Console.WriteLine($"Group {g.Name}: max {g.MaximumSize:N0}");
            foreach (var p in md.Partitions)
            {
                Console.WriteLine($"Partition {p.Name}: attrs {p.Attributes}, group {md.GroupNameOf(p)}, size {md.SizeOf(p):N0}");
                foreach (var e in md.ExtentsOf(p)) Console.WriteLine($"    extent: {e.NumSectors} sectors @ {e.TargetData} (type {e.TargetType}, dev {e.TargetSource})");
            }
            foreach (var w in r.Warnings) Console.WriteLine($"WARNING: {w}");
        }
        else
        {
            var info = AndroidImageInspector.Inspect(f.Stream, positional[0], AndroidImageInspector.Role.System);
            Console.WriteLine(StorageReport.Describe(info));
            foreach (var kv in info.Props.Where(k => k.Key.Contains("version") || k.Key.Contains("abi") || k.Key.Contains("vndk")).Take(30))
                Console.WriteLine($"  {kv.Key}={kv.Value}");
            foreach (var n in info.Notes) Console.WriteLine($"  note: {n}");
        }
        return 0;
    }

    if (cmd == "patch-file")
    {
        var r = Ext4FilePatcher.PatchCopy(Opt("image") ?? throw new ToolkitException("--image is required."),
            Opt("output") ?? throw new ToolkitException("--output is required."),
            Opt("path") ?? throw new ToolkitException("--path is required."),
            File.ReadAllBytes(Opt("with") ?? throw new ToolkitException("--with is required.")));
        Console.WriteLine($"Patched {r.FilePath}: inode {r.Inode}, size {r.OldSize} -> {r.NewSize}, data blocks {string.Join(",", r.DataBlocks)}");
        Console.WriteLine($"  old sha256 {r.OldSha256}\n  new sha256 {r.NewSha256}");
        Console.WriteLine($"  changed filesystem blocks: {string.Join(", ", r.ChangedBlocks)}");
        Console.WriteLine($"Verified: {r.ImagePath}");
        return 0;
    }

    if (cmd == "lpmake")
    {
        var size = ulong.Parse(Opt("device-size") ?? throw new ToolkitException("--device-size is required."));
        var output = Opt("output") ?? throw new ToolkitException("--output is required.");
        var parts = positional.Select(a => { var eq = a.IndexOf('='); return (Name: a[..eq], Path: a[(eq + 1)..]); }).ToList();
        var files = parts.Select(p => ImageFile.Open(p.Path)).ToList();
        try
        {
            ulong sum = files.Aggregate(0UL, (a, f) => a + (ulong)f.Length);
            var layout = LpLayoutPlanner.Plan(new LpLayoutRequest
            {
                DeviceSize = size, MetadataMaxSize = 65536, MetadataSlotCount = 1,
                Groups = { new LpGroupRequest("main", sum) },
                Partitions = parts.Select((p, i) => new LpPartitionRequest(p.Name, "main", LpFormat.AttrReadOnly, (ulong)files[i].Length)).ToList(),
            });
            if (!layout.Success) throw new ToolkitException(layout.Error!);
            using var fs = SafeFile.CreateNew(output);
            IDeviceImageWriter w = flags.Contains("sparse") ? new SparseImageWriter(fs, 4096, size / 4096) : new RawImageWriter(fs, 4096, size / 4096);
            var r = SuperImageBuilder.Build(layout.Metadata!, parts.Select((p, i) => new PartitionContent { Name = p.Name, Length = files[i].Length, Open = () => new SubStream(files[i].Stream, 0, files[i].Length) }).ToList(), w);
            Console.WriteLine($"Wrote {output}: {r.OutputFileSize:N0} bytes");
        }
        finally { foreach (var f in files) f.Dispose(); }
        return 0;
    }

    var profileName = cmd == "unpack" ? "x04g" : Opt("profile") ?? "x04g";
    var profile = profileName.ToLowerInvariant() switch
    {
        "x04g" => RomProfileCatalog.X04G,
        "lx04" => RomProfileCatalog.LX04,
        "gsi" => RomProfileCatalog.Gsi,
        "custom" => RomProfileCatalog.Custom,
        _ => throw new ToolkitException($"Unknown profile '{profileName}'."),
    };
    var source = Opt("source") ?? throw new ToolkitException("--source is required.");
    var req = new PreparationRequest
    {
        Profile = profile,
        SourcePath = source,
        OutputDirectory = Opt("out") ?? Directory.GetCurrentDirectory(),
        ExtractPartitions = cmd == "unpack" || flags.Contains("extract"),
        BuildSuper = cmd == "build",
        ShrinkExt4 = flags.Contains("shrink"),
        Format = flags.Contains("raw") ? OutputFormat.Raw : OutputFormat.Sparse,
        VerifyOutput = !flags.Contains("no-verify"),
        BootImagePath = Opt("boot"),
        PatchBoot = flags.Contains("patch-boot"),
        CleanupIntermediates = flags.Contains("cleanup"),
        DryRun = flags.Contains("dry-run"),
    };
    if (Opt("system") is string sys) req.Replacements["system"] = sys;
    foreach (var r in opts.GetValueOrDefault("replace") ?? new())
    {
        var eq = r.IndexOf('=');
        if (eq <= 0) throw new ToolkitException($"--replace expects part=path, got '{r}'.");
        req.Replacements[r[..eq]] = r[(eq + 1)..];
    }
    foreach (var pt in opts.GetValueOrDefault("patch") ?? new())
    {
        int c = pt.IndexOf(':'), eq = pt.IndexOf('=');
        if (c <= 0 || eq <= c) throw new ToolkitException($"--patch expects part:/path=file, got '{pt}'.");
        req.FilePatches.Add(new FilePatch(pt[..c], pt[(c + 1)..eq], pt[(eq + 1)..]));
    }
    if (profile.RemovesProductByDefault && !flags.Contains("keep-product")) req.RemovePartitions.Add("product");
    foreach (var r in opts.GetValueOrDefault("remove") ?? new()) req.RemovePartitions.Add(r);

    IExt4Toolchain? tc = null;
    if (Opt("tools") is string td) { tc = new NativeExt4Toolchain(td); if (tc.Probe() is string why) { Console.Error.WriteLine($"ext4 tools unusable: {why}"); tc = null; } }
    else if (req.ShrinkExt4) tc = Ext4ToolchainLocator.Locate(AppContext.BaseDirectory).Toolchain;

    using var log = new OperationLog();
    log.EntryAdded += e => { if (e.Level >= LogLevel.Info) Console.Error.WriteLine(e.ToString()); };
    var report = StorageAnalyzer.Analyze(req, tc, log);
    Console.WriteLine(report.ToText());
    if (cmd == "analyze") return report.Overall == Verdict.Fail ? 1 : 0;

    var plan = PreparationPlanner.Plan(req, report, tc != null);
    Console.WriteLine(plan.ToText());
    if (!plan.CanExecute) return 1;
    var exec = new PreparationExecutor(log, tc);
    var res = exec.Execute(plan);
    Console.WriteLine(res.FinalReport);
    if (!res.Success) { Console.Error.WriteLine($"FAILED: {res.Error}"); return 1; }
    return 0;
}
catch (ToolkitException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
