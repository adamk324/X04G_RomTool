using System.Text;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;

namespace X04G.Core.RomProfiles;

/// <summary>Facts read from a system/vendor/product filesystem image. Null means "could not be determined".</summary>
public sealed class AndroidImageInfo
{
    public string Label { get; init; } = "";
    public long ImageLength { get; init; }
    public FileSystemKind FileSystem { get; init; }
    public ulong? Ext4FilesystemBytes { get; init; }
    public ulong? Ext4UsedBytes { get; init; }
    public uint? Ext4BlockSize { get; init; }
    public bool Ext4SharedBlocks { get; init; }
    public IReadOnlyList<string> Ext4Features { get; init; } = Array.Empty<string>();
    public string? VolumeName { get; init; }
    public bool? SystemAsRoot { get; init; }
    public string? BuildPropPath { get; init; }
    public Dictionary<string, string> Props { get; init; } = new();
    /// <summary>VNDK versions shipped in a system image (e.g. "29", "current"); empty if none found or not a system image.</summary>
    public IReadOnlyList<string> VndkProvided { get; init; } = Array.Empty<string>();
    public List<string> Notes { get; } = new();

    public string? Prop(params string[] keys)
    {
        foreach (var k in keys) if (Props.TryGetValue(k, out var v) && v.Length > 0) return v;
        return null;
    }

    public int? Sdk => int.TryParse(Prop("ro.build.version.sdk", "ro.system.build.version.sdk", "ro.vendor.build.version.sdk"), out var v) ? v : null;
    public string? Release => Prop("ro.build.version.release", "ro.system.build.version.release", "ro.vendor.build.version.release");
    public string? AbiList => Prop("ro.system.product.cpu.abilist", "ro.product.cpu.abilist", "ro.vendor.product.cpu.abilist", "ro.product.vendor.cpu.abilist", "ro.product.cpu.abi");
    public string? Abi64List => Prop("ro.system.product.cpu.abilist64", "ro.product.cpu.abilist64", "ro.vendor.product.cpu.abilist64", "ro.product.vendor.cpu.abilist64");
    public string? Abi32List => Prop("ro.system.product.cpu.abilist32", "ro.product.cpu.abilist32", "ro.vendor.product.cpu.abilist32", "ro.product.vendor.cpu.abilist32");
    public string? VndkVersion => Prop("ro.vndk.version", "ro.vendor.vndk.version");
    public string? Fingerprint => Prop("ro.system.build.fingerprint", "ro.build.fingerprint", "ro.vendor.build.fingerprint");
    public string? Description => Prop("ro.build.display.id", "ro.system.build.id", "ro.build.id", "ro.vendor.build.id");
    public string? TrebleEnabled => Prop("ro.treble.enabled");
    public string? Device => Prop("ro.product.vendor.device", "ro.product.device", "ro.product.system.device");

    public bool Supports64 => (AbiList ?? "").Contains("arm64-v8a") || (Abi64List ?? "").Contains("arm64-v8a");
    public bool Supports32 => (AbiList ?? "").Contains("armeabi") || (Abi32List ?? "").Length > 0;
}

public static class AndroidImageInspector
{
    public enum Role { System, Vendor, Product, Other }

    public static AndroidImageInfo Inspect(Stream image, string label, Role role)
    {
        var fsKind = FileSystemDetector.Detect(image);
        if (fsKind != FileSystemKind.Ext4)
        {
            var info = new AndroidImageInfo { Label = label, ImageLength = image.Length, FileSystem = fsKind };
            info.Notes.Add(fsKind switch
            {
                FileSystemKind.Erofs => "EROFS filesystem: the toolkit cannot read EROFS contents, so build properties were not checked. EROFS images cannot be shrunk.",
                FileSystemKind.Empty => "The image starts with zeros — it may be empty or not a filesystem image.",
                FileSystemKind.Unknown => "Unrecognized filesystem.",
                _ => $"{fsKind} filesystem: contents were not inspected.",
            });
            return info;
        }

        Ext4Image ext;
        try { ext = new Ext4Image(image); }
        catch (Exception ex)
        {
            var bad = new AndroidImageInfo { Label = label, ImageLength = image.Length, FileSystem = fsKind };
            bad.Notes.Add($"ext4 superblock could not be parsed: {ex.Message}");
            return bad;
        }

        string? propPath = null;
        Dictionary<string, string> props = new();
        bool? sar = null;
        var notes = new List<string>();
        var vndk = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            if (role == Role.System)
            {
                bool hasSysDir = ext.IsDirectory("/system");
                bool hasNestedProp = ext.Exists("/system/build.prop");
                bool hasRootInit = ext.Exists("/init") || ext.Exists("/system/bin/init");
                sar = hasSysDir && hasNestedProp;
                if (!hasNestedProp && ext.Exists("/build.prop")) sar = false;
                foreach (var p in new[] { "/system/build.prop", "/build.prop" })
                {
                    var d = ext.ReadFile(p);
                    if (d != null) { propPath = p; props = BuildProp.Parse(Encoding.UTF8.GetString(d)); break; }
                }
                if (!hasRootInit) notes.Add("No /init or /system/bin/init found in the image.");
                string root = sar == true ? "/system" : "";
                foreach (var dir in new[] { $"{root}/apex", $"{root}/system_ext/apex" })
                    foreach (var n in ext.ListDirectory(dir))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(n, @"^com\.android\.vndk\.(v(\d+)|current)(\.apex|\.capex)?$");
                        if (m.Success) vndk.Add(m.Groups[2].Success ? m.Groups[2].Value : "current");
                    }
                foreach (var dir in new[] { $"{root}/lib", $"{root}/lib64" })
                    foreach (var n in ext.ListDirectory(dir))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(n, @"^vndk-(sp-)?(\d+)$");
                        if (m.Success) vndk.Add(m.Groups[2].Value);
                        else if (n == "vndk" || n == "vndk-sp") vndk.Add("current");
                    }
            }
            else
            {
                foreach (var p in new[] { "/build.prop", "/etc/build.prop" })
                {
                    var d = ext.ReadFile(p);
                    if (d != null) { propPath = p; props = BuildProp.Parse(Encoding.UTF8.GetString(d)); break; }
                }
            }
            if (propPath == null) notes.Add("No build.prop found.");
        }
        catch (Exception ex)
        {
            notes.Add($"Could not read files from the ext4 image: {ex.Message}");
        }

        var res = new AndroidImageInfo
        {
            Label = label,
            ImageLength = image.Length,
            FileSystem = fsKind,
            Ext4FilesystemBytes = ext.FilesystemBytes,
            Ext4UsedBytes = ext.UsedBytes,
            Ext4BlockSize = ext.BlockSize,
            Ext4SharedBlocks = ext.HasSharedBlocks,
            Ext4Features = ext.FeatureNames(),
            VolumeName = ext.VolumeName,
            SystemAsRoot = sar,
            BuildPropPath = propPath,
            Props = props,
            VndkProvided = vndk.ToList(),
        };
        res.Notes.AddRange(notes);
        if ((ulong)image.Length < ext.FilesystemBytes)
            res.Notes.Add($"The image ({image.Length:N0} bytes) is shorter than its ext4 filesystem ({ext.FilesystemBytes:N0} bytes) — it is truncated or corrupt.");
        return res;
    }

    public static AndroidImageInfo InspectFile(string path, string label, Role role)
    {
        using var f = ImageFile.Open(path);
        return Inspect(f.Stream, label, role);
    }
}
