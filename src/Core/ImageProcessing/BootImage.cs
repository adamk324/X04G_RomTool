using System.Buffers.Binary;
using System.Text;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

/// <summary>
/// Android boot image header reader and kernel command line patcher (header versions 0-4).
/// Replaces the "unpack with Android Image Kitchen, edit cmdline, repack" step of WorkingWithGSI.md:
/// only the cmdline field is changed; kernel, ramdisk and everything else stay byte-identical.
/// </summary>
public sealed class BootImageInfo
{
    public uint HeaderVersion { get; init; }
    public uint PageSize { get; init; }
    public uint KernelSize { get; init; }
    public uint RamdiskSize { get; init; }
    public string Cmdline { get; init; } = "";
    public string ExtraCmdline { get; init; } = "";
    public int CmdlineOffset { get; init; }
    public int CmdlineCapacity { get; init; }
    public string Name { get; init; } = "";
}

public static class BootImage
{
    public static readonly byte[] Magic = "ANDROID!"u8.ToArray();

    /// <summary>Documented in howtos/WorkingWithGSI.md (original repository).</summary>
    public const string DocumentedOriginalCmdline = "bootopt=64S3,32N2,32N2 buildvariant=user";
    public const string DocumentedGsiCmdline = "bootopt=64S3,32N2,32N2 buildvariant=userdebug enforcing=0 androidboot.selinux=permissive";

    public static BootImageInfo Read(Stream s)
    {
        var h = new byte[4096];
        s.Position = 0;
        int n = s.ReadAtLeast(h, h.Length, false);
        if (n < 1632 || !h.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidImageException("Not an Android boot image (missing ANDROID! magic).");
        var b = h.AsSpan();
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(b[40..]);
        if (version >= 3)
        {
            if (version > 4) throw new InvalidImageException($"Unsupported boot image header version {version}.");
            return new BootImageInfo
            {
                HeaderVersion = version,
                PageSize = 4096,
                KernelSize = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
                RamdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
                Cmdline = CString(b.Slice(44, 1536)),
                CmdlineOffset = 44,
                CmdlineCapacity = 1536,
            };
        }
        // v0-v2: header_version field at 40 (v0 images store 0 there, older ones may store garbage — treat >4 as v0).
        return new BootImageInfo
        {
            HeaderVersion = version,
            KernelSize = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            RamdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            PageSize = BinaryPrimitives.ReadUInt32LittleEndian(b[36..]),
            Name = CString(b.Slice(48, 16)),
            Cmdline = CString(b.Slice(64, 512)),
            ExtraCmdline = CString(b.Slice(608, 1024)),
            CmdlineOffset = 64,
            CmdlineCapacity = 512,
        };
    }

    private static string CString(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? b : b[..n]);
    }

    /// <summary>
    /// Computes the GSI command line from the documented edit: replace "buildvariant=user" with
    /// "buildvariant=userdebug enforcing=0 androidboot.selinux=permissive". Returns null if the
    /// cmdline does not contain the documented token (the caller must then not patch automatically).
    /// </summary>
    public static string? ComputeGsiCmdline(string current, out bool exactDocumentedMatch, out bool alreadyPatched)
    {
        exactDocumentedMatch = current.Trim() == DocumentedOriginalCmdline;
        alreadyPatched = current.Contains("androidboot.selinux=permissive");
        if (alreadyPatched) return current;
        var tokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int i = tokens.IndexOf("buildvariant=user");
        if (i < 0) return null;
        tokens[i] = "buildvariant=userdebug";
        tokens.Insert(i + 1, "enforcing=0");
        tokens.Insert(i + 2, "androidboot.selinux=permissive");
        return string.Join(' ', tokens);
    }

    /// <summary>Copies <paramref name="inputPath"/> to a new file with only the cmdline replaced.</summary>
    public static void WritePatchedCopy(string inputPath, string outputPath, string newCmdline)
    {
        using var src = SafeFile.OpenReadOnly(inputPath);
        var info = Read(src);
        var bytes = Encoding.ASCII.GetBytes(newCmdline);
        if (bytes.Length >= info.CmdlineCapacity)
            throw new ToolkitException($"New command line is {bytes.Length} bytes; the boot header only has room for {info.CmdlineCapacity - 1}.");
        using var dst = SafeFile.CreateNew(outputPath);
        src.Position = 0;
        src.CopyTo(dst);
        var field = new byte[info.CmdlineCapacity];
        bytes.CopyTo(field, 0);
        dst.Position = info.CmdlineOffset;
        dst.Write(field);
        dst.Flush();
    }
}
