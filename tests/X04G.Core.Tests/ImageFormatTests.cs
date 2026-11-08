using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;

namespace X04G.Core.Tests;

public static class SparseTests
{
    private static byte[] Sample(int blocks)
    {
        var data = new byte[blocks * 4096];
        var rnd = new Random(42);
        for (int b = 0; b < blocks; b++)
        {
            var span = data.AsSpan(b * 4096, 4096);
            switch (b % 5)
            {
                case 0: rnd.NextBytes(span); break;           // raw
                case 1: break;                                 // zeros → fill
                case 2: for (int i = 0; i < 4096; i += 4) BitConverter.GetBytes(0xDEADBEEFu).CopyTo(span[i..]); break; // pattern fill
                default: rnd.NextBytes(span); break;
            }
        }
        return data;
    }

    [Test]
    public static void RoundTrip_WriterAndReader()
    {
        var data = Sample(64);
        var path = Path.Combine(Fixtures.NewDir(), "s.img");
        using (var fs = File.Create(path))
        {
            var w = new SparseImageWriter(fs, 4096, 80); // 16 trailing don't-care blocks
            for (int b = 0; b < 64; b++)
            {
                var blk = data.AsSpan(b * 4096, 4096);
                if (b % 5 == 1) w.FillBlocks((ulong)b, 1, 0);
                else if (b % 5 == 2) w.FillBlocks((ulong)b, 1, 0xDEADBEEF);
                else w.WriteBlocks((ulong)b, blk);
            }
            w.Finish();
        }
        using var f = ImageFile.Open(path);
        Assert.True(f.IsSparse);
        Assert.Equal(80L * 4096, f.Length);
        var back = new byte[f.Length];
        f.Stream.ReadExactly(back);
        Assert.SeqEqual(data, back.AsSpan(0, data.Length));
        Assert.True(back.AsSpan(data.Length).IndexOfAnyExcept((byte)0) < 0, "don't-care tail must read as zeros");
    }

    [Test]
    public static void Reader_MatchesIndependentSimg2img()
    {
        var simg2img = Fixtures.FindTool("simg2img");
        var img2simg = Fixtures.FindTool("img2simg");
        if (simg2img == null || img2simg == null) throw new SkipException("simg2img/img2simg (android-sdk-libsparse-utils) not installed");
        var dir = Fixtures.NewDir();
        var raw = Path.Combine(dir, "raw.img");
        File.WriteAllBytes(raw, Sample(100));
        var sp = Path.Combine(dir, "sp.img");
        Assert.Equal(0, Fixtures.Run(img2simg, raw, sp).Code, "img2simg");
        using var f = ImageFile.Open(sp);
        Assert.True(f.IsSparse);
        Assert.SeqEqual(Fixtures.Sha(raw), Fixtures.Sha(f.Stream, f.Length), "reader vs original");

        // And the other direction: our writer's output expanded by simg2img.
        var mine = Fixtures.StockSuper(sparse: true, dir: Fixtures.NewDir());
        var expanded = Path.Combine(dir, "exp.img");
        Assert.Equal(0, Fixtures.Run(simg2img, mine, expanded).Code, "simg2img");
        using var g = ImageFile.Open(mine);
        Assert.SeqEqual(Fixtures.Sha(expanded), Fixtures.Sha(g.Stream, g.Length), "simg2img expansion vs our reader");
    }

    [Test]
    public static void Reader_RejectsCorruptImages()
    {
        var dir = Fixtures.NewDir();
        var path = Path.Combine(dir, "s.img");
        using (var fs = File.Create(path)) { var w = new SparseImageWriter(fs, 4096, 10); w.WriteBlocks(0, new byte[4096 * 10]); w.Finish(); }
        var bytes = File.ReadAllBytes(path);
        var truncated = Path.Combine(dir, "trunc.img");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidImageException>(() => ImageFile.Open(truncated).Dispose());
        var badChunk = (byte[])bytes.Clone();
        badChunk[28] = 0x99; badChunk[29] = 0x99; // first chunk type
        var bc = Path.Combine(dir, "badchunk.img");
        File.WriteAllBytes(bc, badChunk);
        Assert.Throws<InvalidImageException>(() => ImageFile.Open(bc).Dispose());
        var empty = Path.Combine(dir, "empty.img");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Throws<InvalidImageException>(() => ImageFile.Open(empty).Dispose());
    }
}

public static class LpMetadataTests
{
    [Test]
    public static void Planner_MatchesLpmakeLayoutRules()
    {
        // Parameters of the original scripts: --metadata-size 65536 --metadata-slots 1, lpmake default 1 MiB alignment
        var r = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 64 << 20,
            Groups = { new LpGroupRequest("main", 0) },
            Partitions =
            {
                new LpPartitionRequest("system", "main", 1, 10_000_000),
                new LpPartitionRequest("vendor", "main", 1, 3 << 20),
                new LpPartitionRequest("product", "main", 1, 4096),
            },
        });
        Assert.True(r.Success, r.Error ?? "");
        var md = r.Metadata!;
        // 4096 + (4096 + 65536) * 2 = 143360 → aligned to 1 MiB = sector 2048
        Assert.Equal(2048UL, md.SuperDevice.FirstLogicalSector);
        var sys = md.ExtentsOf(md.FindPartition("system")!).Single();
        Assert.Equal(2048UL, sys.TargetData);
        Assert.Equal(ByteSize.AlignUp(10_000_000, 4096) / 512, sys.NumSectors, "system rounded up to 4 KiB");
        var ven = md.ExtentsOf(md.FindPartition("vendor")!).Single();
        Assert.Equal(0UL, ven.TargetData * 512 % (1 << 20), "vendor starts on a 1 MiB boundary");
        Assert.True(ven.TargetData * 512 >= (sys.TargetData + sys.NumSectors) * 512);
        Assert.Equal("default", md.Groups[0].Name);
        Assert.Equal(1u, md.FindPartition("vendor")!.GroupIndex);
    }

    [Test]
    public static void Planner_ReportsInsufficientSpace()
    {
        var r = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 8 << 20,
            Groups = { new LpGroupRequest("main", 0) },
            Partitions = { new LpPartitionRequest("system", "main", 1, 6 << 20), new LpPartitionRequest("vendor", "main", 1, 2 << 20) },
        });
        Assert.False(r.Success);
        Assert.Contains("vendor", r.Error!);
    }

    [Test]
    public static void Planner_ExactFitAndGroupLimit()
    {
        // 8 MiB device: 1 MiB metadata area + 4 + 2 + 1 MiB partitions = exactly full.
        var ok = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 8 << 20,
            Groups = { new LpGroupRequest("main", 7 << 20) },
            Partitions = { new("system", "main", 1, 4 << 20), new("vendor", "main", 1, 2 << 20), new("product", "main", 1, 1 << 20) },
        });
        Assert.True(ok.Success, ok.Error ?? "");
        Assert.Equal(0UL, ok.FreeAfterAllocation);
        var grp = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 64 << 20,
            Groups = { new LpGroupRequest("main", 5 << 20) },
            Partitions = { new("system", "main", 1, 4 << 20), new("vendor", "main", 1, 2 << 20) },
        });
        Assert.False(grp.Success);
        Assert.Contains("group", grp.Error!);
    }

    [Test]
    public static void SerializeParse_RoundTripAndChecksums()
    {
        var path = Fixtures.StockSuper();
        using var img = SuperImage.Open(path);
        Assert.Equal(0, img.Warnings.Count, string.Join(";", img.Warnings));
        var md = img.Metadata;
        Assert.Equal(10, (int)md.MajorVersion);
        Assert.Equal(0, (int)md.MinorVersion);
        Assert.Equal(65536u, md.Geometry.MetadataMaxSize);
        Assert.Equal("super", md.SuperDevice.PartitionName);
        Assert.Equal(new[] { "system", "vendor", "product" }.Aggregate((a, b) => a + "," + b), string.Join(",", md.Partitions.Select(p => p.Name)));
        // Contents of each partition equal the fixture images
        foreach (var (name, fx) in new[] { ("system", "system_stock.img"), ("vendor", "vendor_stock.img"), ("product", "product_stock.img") })
        {
            var f = Fixtures.Ext4(fx);
            using var ps = img.OpenPartition(name);
            Assert.SeqEqual(Fixtures.Sha(f), Fixtures.Sha(ps, new FileInfo(f).Length), name);
        }
    }

    [Test]
    public static void Reader_FallsBackToBackupCopies()
    {
        var path = Fixtures.StockSuper();
        var bytes = File.ReadAllBytes(path);
        // Corrupt primary geometry and primary metadata (one byte each)
        bytes[4096 + 50] ^= 0xFF;
        bytes[4096 + 8192 + 200] ^= 0xFF;
        var p2 = Path.Combine(Fixtures.NewDir(), "corrupt.bin");
        File.WriteAllBytes(p2, bytes);
        using var img = SuperImage.Open(p2);
        Assert.True(img.Warnings.Any(w => w.Contains("geometry")), "geometry warning");
        Assert.True(img.Warnings.Any(w => w.Contains("metadata")), "metadata warning");
        Assert.Equal(3, img.Metadata.Partitions.Count);

        // Destroy all copies → clear error
        var md = img.Metadata;
        var g = md.Geometry;
        bytes[8192 + 50] ^= 0xFF;
        var p3 = Path.Combine(Fixtures.NewDir(), "dead.bin");
        File.WriteAllBytes(p3, bytes);
        Assert.Throws<InvalidImageException>(() => SuperImage.Open(p3).Dispose());
    }

    [Test]
    public static void IndependentParser_lpunpack_py_AcceptsOutput()
    {
        // Optional cross-check against a third-party implementation (unix3dgforce/lpunpack.py) if provided via X04G_LPUNPACK_PY.
        var py = Environment.GetEnvironmentVariable("X04G_LPUNPACK_PY");
        if (py == null || !File.Exists(py)) throw new SkipException("set X04G_LPUNPACK_PY to lpunpack.py to run this cross-check");
        var python = Fixtures.FindTool("python3") ?? throw new SkipException("python3 not found");
        // Raw super only: lpunpack.py's own sparse expansion ignores FILL chunk values (writes zeros), so it cannot
        // check sparse output. Sparse output is validated against AOSP simg2img in SparseTests instead.
        foreach (bool sparse in new[] { false })
        {
            var dir = Fixtures.NewDir();
            var super = Fixtures.StockSuper(sparse: sparse, dir: dir);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            var (code, output) = Fixtures.Run(python, py, super, outDir);
            Assert.Equal(0, code, output);
            foreach (var (name, fx) in new[] { ("system", "system_stock.img"), ("vendor", "vendor_stock.img"), ("product", "product_stock.img") })
            {
                var extracted = Path.Combine(outDir, name + ".img");
                Assert.True(File.Exists(extracted), $"lpunpack.py did not extract {name}: {output}");
                var f = Fixtures.Ext4(fx);
                Assert.SeqEqual(Fixtures.Sha(f), Fixtures.Sha(File.OpenRead(extracted), new FileInfo(f).Length), $"{name} via lpunpack.py");
            }
        }
    }

    [Test]
    public static void SparseAndRawOutputsExpandIdentically()
    {
        var raw = Fixtures.StockSuper(sparse: false);
        var sp = Fixtures.StockSuper(sparse: true);
        using var a = ImageFile.Open(raw);
        using var b = ImageFile.Open(sp);
        Assert.Equal(a.Length, b.Length);
        Assert.SeqEqual(Fixtures.Sha(a.Stream, a.Length), Fixtures.Sha(b.Stream, b.Length));
        Assert.True(b.FileLength < a.FileLength, "sparse should be smaller");
    }
}

public static class Ext4ReaderTests
{
    [Test]
    public static void ReadsBuildPropAndFollowsSymlinks()
    {
        using var fs = File.OpenRead(Fixtures.Ext4("system_stock.img"));
        var e = new Ext4Image(fs);
        Assert.Equal(4096u, e.BlockSize);
        Assert.Equal(1024UL, e.BlocksCount);
        Assert.True(e.UsedBytes > 300_000 && e.UsedBytes < e.FilesystemBytes);
        var prop = Encoding.UTF8.GetString(e.ReadFile("/system/build.prop")!);
        Assert.Contains("ro.build.version.sdk=29", prop);
        Assert.Equal("init\n", Encoding.UTF8.GetString(e.ReadFile("/init")!), "symlink /init → /system/bin/init");
        Assert.True(e.IsDirectory("/system/apex"));
        Assert.True(e.ReadFile("/does/not/exist") == null);
        var blob = e.ReadFile("/system/blob.bin")!;
        Assert.Equal(300000, blob.Length);
        Assert.True(blob.All(b => b == (byte)'S'));
    }

    [Test]
    public static void ReadsClassicBlockMapsWithIndirectBlocks()
    {
        using var fs = File.OpenRead(Fixtures.Ext4("ext2_blockmap.img"));
        var e = new Ext4Image(fs);
        Assert.Equal(1024u, e.BlockSize);
        var big = e.ReadFile("/d/big.bin")!;
        Assert.Equal(Fixtures.FixtureText("ext2_blockmap.big.sha256"), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(big)).ToLowerInvariant());
        Assert.Equal("hello", Encoding.ASCII.GetString(e.ReadFile("/small.txt")!));
    }

    [Test]
    public static void ReadsInlineData()
    {
        using var fs = File.OpenRead(Fixtures.Ext4("ext4_inline.img"));
        var e = new Ext4Image(fs);
        Assert.Equal("inline!", Encoding.ASCII.GetString(e.ReadFile("/dir/tiny.txt")!));
    }

    [Test]
    public static void DetectsFilesystemsAndArchives()
    {
        using (var fs = File.OpenRead(Fixtures.Ext4("vendor_stock.img"))) Assert.Equal(FileSystemKind.Ext4, FileSystemDetector.Detect(fs));
        var gz = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vendor_stock.img.gz");
        using (var fs = File.OpenRead(gz)) Assert.Equal("gzip", FileSystemDetector.ArchiveKind(fs));
        var erofs = new byte[4096];
        BitConverter.GetBytes(0xE0F5E1E2u).CopyTo(erofs, 1024);
        Assert.Equal(FileSystemKind.Erofs, FileSystemDetector.Detect(new MemoryStream(erofs)));
        Assert.Equal(FileSystemKind.Empty, FileSystemDetector.Detect(new MemoryStream(new byte[4096])));
        Assert.Throws<InvalidImageException>(() => new Ext4Image(new MemoryStream(new byte[4096])));
    }
}

public static class BootImageTests
{
    [Test]
    public static void PatchesDocumentedCmdlineOnly()
    {
        var boot = Fixtures.BootImage(BootImage.DocumentedOriginalCmdline);
        string? n;
        using (var s = File.OpenRead(boot))
        {
            var info = BootImage.Read(s);
            Assert.Equal(BootImage.DocumentedOriginalCmdline, info.Cmdline);
            n = BootImage.ComputeGsiCmdline(info.Cmdline, out bool exact, out bool already);
            Assert.True(exact); Assert.False(already);
        }
        Assert.Equal(BootImage.DocumentedGsiCmdline, n, "documented replacement (WorkingWithGSI.md)");
        var outPath = Path.Combine(Fixtures.NewDir(), "patched.img");
        BootImage.WritePatchedCopy(boot, outPath, n!);
        var a = File.ReadAllBytes(boot); var b = File.ReadAllBytes(outPath);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            if (i < 64 || i >= 64 + 512) Assert.True(a[i] == b[i], $"byte {i} outside cmdline changed");
        using var r = File.OpenRead(outPath);
        Assert.Equal(BootImage.DocumentedGsiCmdline, BootImage.Read(r).Cmdline);
        // Existing output is never overwritten
        Assert.Throws<OutputExistsException>(() => BootImage.WritePatchedCopy(boot, outPath, n!));
    }

    [Test]
    public static void RefusesUnknownCmdlineAndNonBootFiles()
    {
        Assert.True(BootImage.ComputeGsiCmdline("console=ttyS0 foo=bar", out _, out _) == null);
        BootImage.ComputeGsiCmdline("x androidboot.selinux=permissive", out _, out bool already);
        Assert.True(already);
        Assert.Throws<InvalidImageException>(() => BootImage.Read(new MemoryStream(new byte[4096])));
    }
}
