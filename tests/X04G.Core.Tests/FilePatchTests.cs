using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Core.Tests;

public static class FilePatchTests
{
    private static byte[] RemoveFirstCodec(byte[] xml)
    {
        var s = Encoding.UTF8.GetString(xml);
        int a = s.IndexOf("        <MediaCodec name=\"OMX.TEST.DEC.03\"", StringComparison.Ordinal);
        int b = s.IndexOf("</MediaCodec>\n\n", a, StringComparison.Ordinal) + "</MediaCodec>\n\n".Length;
        return Encoding.UTF8.GetBytes(s[..a] + s[b..]);
    }

    [Test]
    public static void InPlacePatch_KeepsInodeBlocksAndXattr_ChangesOnlyExpectedBlocks()
    {
        var img = Fixtures.Ext4("vendor_nocsum.img");
        byte[] orig;
        Ext4Image.FileLayout before;
        using (var f = File.OpenRead(img)) { var e = new Ext4Image(f); orig = e.ReadFile("/etc/codecs.xml")!; before = e.GetFileLayout("/etc/codecs.xml"); }
        Assert.Equal(2, before.Blocks.Count);
        Assert.True(before.Blocks[1] != before.Blocks[0] + 1, "fixture file should be split over two extents");
        var patched = RemoveFirstCodec(orig);
        Assert.True(patched.Length < orig.Length && patched.Length > 4096, "same block count");

        var outImg = Path.Combine(Fixtures.NewDir(), "vendor.patched.img");
        var srcHash = Convert.ToHexString(Fixtures.Sha(img));
        var r = Ext4FilePatcher.PatchCopy(img, outImg, "/etc/codecs.xml", patched);
        Assert.Equal(srcHash, Convert.ToHexString(Fixtures.Sha(img)), "source image untouched");
        Assert.Equal(before.Inode, r.Inode);
        Assert.True(r.ChangedBlocks.All(b => r.DataBlocks.Contains((ulong)b) || b == r.InodeOffset / 4096));
        using (var f = File.OpenRead(outImg))
        {
            var e = new Ext4Image(f);
            Assert.SeqEqual(patched, e.ReadFile("/etc/codecs.xml")!);
            Assert.Contains("ro.vndk.version=29", Encoding.UTF8.GetString(e.ReadFile("/build.prop")!));
        }
        Assert.Throws<OutputExistsException>(() => Ext4FilePatcher.PatchCopy(img, outImg, "/etc/codecs.xml", patched));

        // Independent checks with e2fsprogs when available: fsck clean, SELinux xattr and mode unchanged
        if (Fixtures.FindTool("e2fsck") is string fsck && Fixtures.FindTool("debugfs") is string dbg)
        {
            var (code, outp) = Fixtures.Run(fsck, "-fn", outImg);
            Assert.Equal(0, code, outp);
            var stat = Fixtures.Run(dbg, "-R", "stat /etc/codecs.xml", outImg).Out;
            Assert.Contains("u:object_r:vendor_configs_file:s0", stat);
            Assert.Contains($"Size: {patched.Length}", stat);
            Assert.Contains("Mode:  0644", stat);
        }
    }

    [Test]
    public static void InPlacePatch_RefusesUnsafeCases()
    {
        var img = Fixtures.Ext4("vendor_nocsum.img");
        using (var f = File.OpenRead(img))
        {
            Assert.Contains("block", Ext4FilePatcher.CanPatch(f, "/etc/codecs.xml", 100)!);          // fewer blocks
            Assert.Contains("block", Ext4FilePatcher.CanPatch(f, "/etc/codecs.xml", 20000)!);        // more blocks
            Assert.Contains("not found", Ext4FilePatcher.CanPatch(f, "/etc/missing.xml", 10)!);
        }
        using (var f = File.OpenRead(Fixtures.Ext4("system_stock.img")))
            Assert.Contains("metadata_csum", Ext4FilePatcher.CanPatch(f, "/system/build.prop", 50)!);
    }

    [Test]
    public static void GsiBuild_WithVendorFilePatch_EndToEnd()
    {
        // Stock-like super whose vendor is the no-checksum fixture; build GSI super with an in-place vendor XML patch.
        var dir = Fixtures.NewDir("vpatch");
        var sys = Fixtures.Ext4("system_stock.img");
        var ven = Fixtures.Ext4("vendor_nocsum.img");
        var prod = Fixtures.Ext4("product_stock.img");
        var parts = new[] { ("product", prod), ("vendor", ven), ("system", sys) };   // X04G stock order
        var layout = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 16 << 20, MetadataSlotCount = 2, Alignment = 512,          // X04G stock: 2 slots, 512-byte alignment
            Groups = { new LpGroupRequest("main", 16 << 20) },
            Partitions = parts.Select(p => new LpPartitionRequest(p.Item1, "main", 1, (ulong)new FileInfo(p.Item2).Length)).ToList(),
        });
        Assert.True(layout.Success, layout.Error ?? "");
        var super = Path.Combine(dir, "super.bin");
        using (var fs = File.Create(super))
            SuperImageBuilder.Build(layout.Metadata!, parts.Select(p => new PartitionContent { Name = p.Item1, Length = new FileInfo(p.Item2).Length, Open = () => File.OpenRead(p.Item2) }).ToList(), new RawImageWriter(fs, 4096, (16 << 20) / 4096));

        byte[] orig;
        using (var f = File.OpenRead(ven)) orig = new Ext4Image(f).ReadFile("/etc/codecs.xml")!;
        var patchedFile = Path.Combine(dir, "codecs.xml");
        File.WriteAllBytes(patchedFile, RemoveFirstCodec(orig));

        var req = new PreparationRequest { Profile = RomProfileCatalog.Gsi, SourcePath = super, OutputDirectory = Fixtures.NewDir("out"), BuildSuper = true };
        req.Replacements["system"] = Fixtures.Ext4("gsi_sar_arm64_sdk30.img");
        req.RemovePartitions.Add("product");
        req.FilePatches.Add(new FilePatch("vendor", "/etc/codecs.xml", patchedFile));
        var rep = StorageAnalyzer.Analyze(req, null);
        Assert.True(rep.InputFindings.Any(f => f.Severity == Severity.Pass && f.Message.Contains("can be applied in place")), rep.ToText());
        Assert.False(rep.BlocksExecution, rep.ToText());
        var superHash = Convert.ToHexString(Fixtures.Sha(super));
        var res = new PreparationExecutor(new OperationLog(), null).Execute(PreparationPlanner.Plan(req, rep, false));
        Assert.True(res.Success, res.Error ?? "");
        Assert.Equal(superHash, Convert.ToHexString(Fixtures.Sha(super)), "source super untouched");
        var outSuper = res.Outputs.Single(o => o.Description.StartsWith("New super")).Path;
        using var img = SuperImage.Open(outSuper);
        Assert.Equal(2u, img.Metadata.Geometry.MetadataSlotCount);
        Assert.Equal(512u, img.Metadata.SuperDevice.Alignment);
        using var vs = img.OpenPartition("vendor");
        var e = new Ext4Image(vs);
        Assert.SeqEqual(File.ReadAllBytes(patchedFile), e.ReadFile("/etc/codecs.xml")!);
        Assert.True(File.Exists(Path.Combine(res.RunDirectory!, "work", "vendor.orig.img")), "reference copy kept");
        // Unmodified reference copy equals the stock vendor byte for byte (rollback image)
        Assert.SeqEqual(Fixtures.Sha(ven), Fixtures.Sha(Path.Combine(res.RunDirectory!, "work", "vendor.orig.img")));
    }
}
