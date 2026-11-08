using System.IO.Compression;
using System.Security.Cryptography;
using X04G.Core.ImageProcessing;

namespace X04G.Core.Tests;

/// <summary>Creates temporary test inputs. Everything lives under one temp folder that is deleted at the end.</summary>
public static class Fixtures
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "x04g_tests_" + Environment.ProcessId);
    private static int _n;

    public static string NewDir(string name = "t")
    {
        var d = Path.Combine(Root, $"{name}_{Interlocked.Increment(ref _n)}");
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Cleanup()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    /// <summary>Decompresses Fixtures/&lt;name&gt;.gz into a fresh temp file.</summary>
    public static string Ext4(string name)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".gz");
        var dst = Path.Combine(NewDir("fx"), name);
        using var gz = new GZipStream(File.OpenRead(src), CompressionMode.Decompress);
        using var o = File.Create(dst);
        gz.CopyTo(o);
        return dst;
    }

    public static string FixtureText(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim();

    public static byte[] Sha(string path) { using var f = File.OpenRead(path); return SHA256.HashData(f); }
    public static byte[] Sha(Stream s, long len) => StreamCopy.Hash(s, len, "", null, default);

    /// <summary>
    /// Builds a stock-like super image (system, vendor, product in group "main", 64 KiB metadata, 1 slot,
    /// 1 MiB alignment — the parameters of the original repack scripts) using the toolkit itself.
    /// </summary>
    public static string StockSuper(ulong deviceSize = 16 << 20, bool sparse = false, string? dir = null, bool includeProduct = true)
    {
        dir ??= NewDir("super");
        var parts = new List<(string Name, string Path)> { ("system", Ext4("system_stock.img")), ("vendor", Ext4("vendor_stock.img")) };
        if (includeProduct) parts.Add(("product", Ext4("product_stock.img")));
        ulong sum = parts.Aggregate(0UL, (a, p) => a + (ulong)new FileInfo(p.Path).Length);
        var req = new LpLayoutRequest
        {
            DeviceSize = deviceSize,
            MetadataMaxSize = 65536,
            MetadataSlotCount = 1,
            Groups = { new LpGroupRequest("main", sum) },
            Partitions = parts.Select(p => new LpPartitionRequest(p.Name, "main", LpFormat.AttrReadOnly, (ulong)new FileInfo(p.Path).Length)).ToList(),
        };
        var layout = LpLayoutPlanner.Plan(req);
        if (!layout.Success) throw new InvalidOperationException(layout.Error);
        var path = Path.Combine(dir, sparse ? "super.img" : "super.bin");
        using var fs = File.Create(path);
        IDeviceImageWriter w = sparse ? new SparseImageWriter(fs, 4096, deviceSize / 4096) : new RawImageWriter(fs, 4096, deviceSize / 4096);
        SuperImageBuilder.Build(layout.Metadata!, parts.Select(p => new PartitionContent
        {
            Name = p.Name, Length = new FileInfo(p.Path).Length, Open = () => File.OpenRead(p.Path),
        }).ToList(), w);
        return path;
    }

    public static string? FindTool(string name)
    {
        foreach (var d in new[] { "/usr/sbin", "/sbin", "/usr/bin", "/bin" })
            if (File.Exists(Path.Combine(d, name))) return Path.Combine(d, name);
        return null;
    }

    public static (int Code, string Out) Run(string exe, params string[] args)
    {
        var r = Ext4Tools.RunProcess(exe, args, default, TimeSpan.FromMinutes(5));
        return (r.ExitCode, r.StdOut + r.StdErr);
    }

    /// <summary>Minimal Android boot image (header v0) with the given cmdline.</summary>
    public static string BootImage(string cmdline, string? dir = null)
    {
        dir ??= NewDir("boot");
        var path = Path.Combine(dir, "boot.bin");
        const int page = 2048;
        var kernel = new byte[5000]; new Random(1).NextBytes(kernel);
        var ramdisk = new byte[3000]; new Random(2).NextBytes(ramdisk);
        var img = new byte[page + 3 * page + 2 * page + 8192];
        "ANDROID!"u8.CopyTo(img);
        BitConverter.GetBytes((uint)kernel.Length).CopyTo(img, 8);
        BitConverter.GetBytes(0x40080000u).CopyTo(img, 12);
        BitConverter.GetBytes((uint)ramdisk.Length).CopyTo(img, 16);
        BitConverter.GetBytes((uint)page).CopyTo(img, 36);
        System.Text.Encoding.ASCII.GetBytes(cmdline).CopyTo(img, 64);
        kernel.CopyTo(img, page);
        ramdisk.CopyTo(img, page * 4);
        File.WriteAllBytes(path, img);
        return path;
    }
}
