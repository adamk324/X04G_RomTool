using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Core.Tests;

public static class KnownFixTests
{
    private const string Xml =
        "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n<!-- c -->\n<Included>\n    <Decoders>\n" +
        "        <MediaCodec name=\"A.MP3\" type=\"audio/mpeg\">\n            <Limit name=\"concurrent-instances\" max=\"16\" />\n        </MediaCodec>\n\n" +
        "        <MediaCodec name=\"A.MP2\">\n            <Type name=\"audio/mpeg-L2\" />\n            <Quirk name=\"q\" />\n        </MediaCodec>\n\n" +
        "        <MediaCodec name=\"A.MP1\" type=\"audio/mpeg-L1\" />\n" +
        "        <MediaCodec name=\"A.GSM\" type=\"audio/gsm\">\n            <Limit name=\"concurrent-instances\" max=\"16\" />\n        </MediaCodec>\n" +
        "    </Decoders>\n</Included>\n";

    [Test]
    public static void RemoveCodecs_RemovesOnlyTheNamedElements()
    {
        var outp = Encoding.UTF8.GetString(Mp2CodecFix.RemoveCodecs(Encoding.UTF8.GetBytes(Xml), new[] { "A.MP2", "A.MP1" }));
        Assert.False(outp.Contains("A.MP2") || outp.Contains("A.MP1"), "removed");
        // Everything else is byte-identical: removing the two blocks from the original by hand gives the same text.
        var expected = Xml
            .Replace("        <MediaCodec name=\"A.MP2\">\n            <Type name=\"audio/mpeg-L2\" />\n            <Quirk name=\"q\" />\n        </MediaCodec>\n\n", "")
            .Replace("        <MediaCodec name=\"A.MP1\" type=\"audio/mpeg-L1\" />\n", "");
        Assert.Equal(expected, outp);
        // CRLF files keep CRLF
        var crlf = Xml.Replace("\n", "\r\n");
        var outCrlf = Encoding.UTF8.GetString(Mp2CodecFix.RemoveCodecs(Encoding.UTF8.GetBytes(crlf), new[] { "A.MP2" }));
        Assert.False(outCrlf.Contains("A.MP2"));
        Assert.False(outCrlf.Replace("\r\n", "").Contains('\n'), "no bare LF introduced");
        Assert.Throws<ToolkitException>(() => Mp2CodecFix.RemoveCodecs(Encoding.UTF8.GetBytes(Xml), new[] { "NOT.THERE" }));
    }

    [Test]
    public static void Detect_FindsAffectedCodec_AndRecommendsOnlyForAndroid16()
    {
        using var f = File.OpenRead(Fixtures.Ext4("vendor_mtk_like.img"));
        var fix16 = Mp2CodecFix.Detect(f, 36)!;
        Assert.NotNull(fix16);
        Assert.True(fix16.Recommended);
        Assert.True(fix16.Blocker == null);
        Assert.Equal(1, fix16.Patches.Count);
        Assert.Equal("/etc/media_codecs_test_audio.xml", fix16.Patches[0].PathInImage);
        Assert.Contains("OMX.TEST.AUDIO.DECODER.MP2", fix16.AffectedItems[0]);
        Assert.Contains("-        <MediaCodec name=\"OMX.TEST.AUDIO.DECODER.MP2\">", fix16.DiffText);
        var gen = Encoding.UTF8.GetString(fix16.Patches[0].Content!);
        Assert.False(gen.Contains("audio/mpeg-L2"));
        Assert.Contains("OMX.TEST.AUDIO.DECODER.MP3", gen);
        Assert.Contains("OMX.TEST.AUDIO.DECODER.X30", gen);

        f.Position = 0;
        var fix10 = Mp2CodecFix.Detect(f, 29)!;
        Assert.False(fix10.Recommended, "Android 10 system is not affected");
        f.Position = 0;
        Assert.False(Mp2CodecFix.Detect(f, null)!.Recommended, "unknown SDK: offered, not pre-selected");
    }

    [Test]
    public static void Detect_IgnoresCodecsWithChannelLimit_AndBlocksMixedTypes()
    {
        // A channel-count limit keeps AudioCapabilities' channel range non-empty: not affected.
        var limited = Xml.Replace("<Quirk name=\"q\" />", "<Limit name=\"channel-count\" max=\"2\" />");
        Assert.True(Mp2CodecFix.RemoveCodecs(Encoding.UTF8.GetBytes(limited), new[] { "A.MP1" }).Length > 0);
        Assert.True(Detect(limited).AffectedItems.All(a => !a.Contains("A.MP2")), "limited MP2 not flagged");
        // A codec that also declares an unaffected type is not removed automatically.
        var mixed = Xml.Replace("<Type name=\"audio/mpeg-L2\" />", "<Type name=\"audio/mpeg-L2\" />\n            <Type name=\"audio/mpeg\" />");
        Assert.NotNull(Detect(mixed).Blocker);
    }

    // Runs Detect on a vendor image whose /etc/media_codecs.xml is the given text (built in a temp copy of the fixture).
    private static KnownFixCandidate Detect(string xml)
    {
        var img = Fixtures.Ext4("vendor_mtk_like.img");
        if (Fixtures.FindTool("debugfs") is not string dbg) throw new SkipException("debugfs not installed");
        var dir = Path.GetDirectoryName(img)!;
        var f = Path.Combine(dir, "media_codecs.xml");
        File.WriteAllText(f, xml.Replace("<Included>", "<MediaCodecs>").Replace("</Included>", "</MediaCodecs>"));
        Fixtures.Run(dbg, "-w", "-R", "rm /etc/media_codecs.xml", img);
        Fixtures.Run(dbg, "-w", "-R", $"write {f} /etc/media_codecs.xml", img);
        using var s = File.OpenRead(img);
        return Mp2CodecFix.Detect(s, 36) ?? throw new AssertException("nothing detected");
    }

    private static string BuildSuper(string vendorFixture)
    {
        var dir = Fixtures.NewDir("kf");
        var parts = new[] { ("product", Fixtures.Ext4("product_stock.img")), ("vendor", Fixtures.Ext4(vendorFixture)), ("system", Fixtures.Ext4("system_stock.img")) };
        var layout = LpLayoutPlanner.Plan(new LpLayoutRequest
        {
            DeviceSize = 16 << 20, MetadataSlotCount = 2, Alignment = 512,
            Groups = { new LpGroupRequest("main", 16 << 20) },
            Partitions = parts.Select(p => new LpPartitionRequest(p.Item1, "main", 1, (ulong)new FileInfo(p.Item2).Length)).ToList(),
        });
        var super = Path.Combine(dir, "super.bin");
        using var fs = File.Create(super);
        SuperImageBuilder.Build(layout.Metadata!, parts.Select(p => new PartitionContent { Name = p.Item1, Length = new FileInfo(p.Item2).Length, Open = () => File.OpenRead(p.Item2) }).ToList(),
            new RawImageWriter(fs, 4096, (16 << 20) / 4096));
        return super;
    }

    [Test]
    public static void GsiAnalysis_FlagsBootBlocker_UntilFixEnabled()
    {
        var req = new PreparationRequest { Profile = RomProfileCatalog.Gsi, SourcePath = BuildSuper("vendor_mtk_like.img"), OutputDirectory = Fixtures.NewDir("out"), BuildSuper = true };
        req.Replacements["system"] = Fixtures.Ext4("gsi_sar_arm32_sdk36.img");
        req.RemovePartitions.Add("product");
        var rep = StorageAnalyzer.Analyze(req, null);
        Assert.Equal(1, rep.KnownFixes.Count);
        Assert.True(rep.KnownFixes[0].Recommended);
        Assert.True(rep.CompatibilityFindings.Any(f => f.Severity == Severity.Fail && f.Message.Contains("Known boot blocker")), rep.ToText());
        Assert.Equal(Verdict.Fail, rep.CompatibilityVerdict);

        req.EnabledFixes.Add(Mp2CodecFix.Id);
        var rep2 = StorageAnalyzer.Analyze(req, null);
        Assert.False(rep2.CompatibilityFindings.Any(f => f.Message.Contains("Known boot blocker")));
        Assert.True(rep2.CompatibilityFindings.Any(f => f.Severity == Severity.Pass && f.Message.Contains("Known fix enabled")));
        Assert.True(rep2.InputFindings.Any(f => f.Severity == Severity.Pass && f.Message.Contains("generated by fix 'mp2-codec'")), rep2.ToText());
        Assert.Contains("Known fix available", rep2.ToText());
    }

    [Test]
    public static void GsiBuild_WithMp2Fix_EndToEnd()
    {
        var super = BuildSuper("vendor_mtk_like.img");
        var req = new PreparationRequest { Profile = RomProfileCatalog.Gsi, SourcePath = super, OutputDirectory = Fixtures.NewDir("out"), BuildSuper = true };
        req.Replacements["system"] = Fixtures.Ext4("gsi_sar_arm32_sdk36.img");
        req.RemovePartitions.Add("product");
        req.EnabledFixes.Add(Mp2CodecFix.Id);
        var superHash = Convert.ToHexString(Fixtures.Sha(super));
        var rep = StorageAnalyzer.Analyze(req, null);
        var plan = PreparationPlanner.Plan(req, rep, false);
        Assert.True(plan.Operations.Any(o => o.Description.Contains("generated by fix 'mp2-codec'")), plan.ToText());
        var res = new PreparationExecutor(new OperationLog(), null).Execute(plan);
        Assert.True(res.Success, res.Error ?? "");
        Assert.Equal(superHash, Convert.ToHexString(Fixtures.Sha(super)), "source untouched");

        using var img = SuperImage.Open(res.Outputs.Single(o => o.Description.StartsWith("New super")).Path);
        using var vs = img.OpenPartition("vendor");
        var e = new Ext4Image(vs);
        var xml = Encoding.UTF8.GetString(e.ReadFile("/etc/media_codecs_test_audio.xml")!);
        Assert.False(xml.Contains("audio/mpeg-L2"), "MP2 removed in the built vendor");
        Assert.Contains("OMX.TEST.AUDIO.DECODER.MP3", xml);
        Assert.Contains("<Include href=\"media_codecs_test_audio.xml\" />", Encoding.UTF8.GetString(e.ReadFile("/etc/media_codecs.xml")!));
        Assert.True(res.Outputs.Any(o => o.Description.StartsWith("Generated /etc/media_codecs_test_audio.xml")), "generated file kept");
        Assert.True(File.Exists(Path.Combine(res.RunDirectory!, "work", "vendor.orig.img")));

        // After the fix, the built vendor no longer triggers detection.
        vs.Position = 0;
        Assert.True(Mp2CodecFix.Detect(vs, 36) == null);
    }
}
