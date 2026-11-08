namespace X04G.Core.RomProfiles;

public enum RomProfileId { X04GGlobal, LX04Chinese, Gsi, CustomSystem }

public enum DocumentationStatus
{
    /// <summary>The procedure is documented and scripted in the original x04g_tools repository.</summary>
    Documented,
    /// <summary>The procedure is documented, but results depend on the specific image (no guarantee it boots).</summary>
    DocumentedExperimental,
    /// <summary>Not documented in the original repository. The toolkit only analyzes; nothing is claimed about booting.</summary>
    NotDocumented,
}

public enum InputKind { SuperBin, SuperImg, SystemImage, BootImage }

public sealed record RequiredInput(InputKind Kind, string Label, string Description, bool Optional);

public sealed class RomProfile
{
    public required RomProfileId Id { get; init; }
    public required string Name { get; init; }
    public required string AndroidVersion { get; init; }
    public required string DeviceVariant { get; init; }
    public required DocumentationStatus Status { get; init; }
    public required string StatusText { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<RequiredInput> Inputs { get; init; }
    public required string StorageNotes { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
    public required string SourceReference { get; init; }

    /// <summary>True when the profile replaces the system partition with a user-provided image.</summary>
    public bool ReplacesSystem => Id is RomProfileId.Gsi or RomProfileId.CustomSystem;
    /// <summary>Product removal is documented for GSI preparation only (WorkingWithGSI.md / unpack_gsi_super.sh).</summary>
    public bool RemovesProductByDefault => Id == RomProfileId.Gsi;
    public bool OffersBootPatch => Id is RomProfileId.Gsi or RomProfileId.CustomSystem;

    public override string ToString() => Name;
}

public static class RomProfileCatalog
{
    private const string SuperDesc =
        "Your own super partition, either super.bin from an MTKClient \"Read partition(s)\" backup (raw) or a super.img produced by this toolkit or the original scripts (sparse or raw).";

    public static readonly RomProfile X04G = new()
    {
        Id = RomProfileId.X04GGlobal,
        Name = "Xiaomi X04G Global ROM",
        AndroidVersion = "Android Go 10 (Google Assistant)",
        DeviceVariant = "X04G (global). The repository states the LX04 shares the same hardware.",
        Status = DocumentationStatus.Documented,
        StatusText = "Officially documented (UnpackRepackSuper.md, unpack/repack_*_super.sh)",
        Summary = "Unpack the stock super partition into system, vendor and product, optionally replace any of them with your own modified image, and repack a new super image. This is the stock workflow of the original repository.",
        Inputs = new[]
        {
            new RequiredInput(InputKind.SuperBin, "Super image", SuperDesc, false),
        },
        StorageNotes = "Capacity is read from the logical-partition metadata of your own super image. The repository does not publish super/partition sizes; the whole flash backup is described as close to 4 GB.",
        Caveats = new[]
        {
            "There is no official firmware download for X04G. Keep your untouched MTKClient backup; it is the only way to restore the device.",
            "Do not share your backup: it contains proprietary files and unique device IDs (serial, Wi-Fi/BT MAC).",
        },
        SourceReference = "howtos/UnpackRepackSuper.md, howtos/BackupFlash.md",
    };

    public static readonly RomProfile LX04 = new()
    {
        Id = RomProfileId.LX04Chinese,
        Name = "Xiaomi LX04 Chinese ROM",
        AndroidVersion = "Android 8.1 (Xiao AI)",
        DeviceVariant = "LX04 (China). The repository states the X04G shares the same hardware.",
        Status = DocumentationStatus.NotDocumented,
        StatusText = "Not documented: analysis only",
        Summary = "The original repository says that both variants share the same hardware and that you can install either variant's software, but it does not describe a procedure or provide scripts for LX04 images. The toolkit can analyze an LX04 super/system image you supply, but it will not claim compatibility.",
        Inputs = new[]
        {
            new RequiredInput(InputKind.SuperImg, "LX04 image", "An LX04 super image (if it has one) or a system image from your own LX04 backup.", false),
        },
        StorageNotes = "Unknown. Android 8.1 predates dynamic partitions (introduced with Android 10), so an LX04 ROM is not expected to use a super partition. The toolkit reports what it finds in the file you supply and never reports PASS for converting between partition layouts.",
        Caveats = new[]
        {
            "No conversion between the LX04 and X04G partition layouts is implemented or documented.",
            "Flashing another variant's software is not covered by the original guides; use MTKClient backups only.",
        },
        SourceReference = "README.md (variant description only)",
    };

    public static readonly RomProfile Gsi = new()
    {
        Id = RomProfileId.Gsi,
        Name = "Generic System Image (GSI)",
        AndroidVersion = "Depends on the image (read from its build.prop)",
        DeviceVariant = "X04G with the stock Android 10 vendor partition",
        Status = DocumentationStatus.DocumentedExperimental,
        StatusText = "Documented procedure, experimental per image (WorkingWithGSI.md)",
        Summary = "Build a super image that keeps your stock vendor partition, drops product (documented, to make room) and uses a GSI as system. Optionally patch boot.img so SELinux is permissive, which the repository says GSIs need to boot.",
        Inputs = new[]
        {
            new RequiredInput(InputKind.SuperImg, "Base super image", "A stock super image (super.bin, or a super.img you built and verified boots). Its vendor partition is reused. " + SuperDesc, false),
            new RequiredInput(InputKind.SystemImage, "GSI system image", "The GSI .img you downloaded (raw or sparse; decompress .xz/.gz/.zip first).", false),
            new RequiredInput(InputKind.BootImage, "boot.bin / boot.img", "Your own boot partition backup, if you want the documented SELinux-permissive cmdline patch.", true),
        },
        StorageNotes = "The GSI must fit in the super partition next to vendor. The repository explains that GSIs are larger than the stock system partition, which is why product is removed. Exact numbers come from your images.",
        Caveats = new[]
        {
            "Not every GSI is compatible. The toolkit checks architecture, Android/VNDK versions and layout from the image files, but a passing check does not mean the GSI boots.",
            "The repository reports that GSIs only boot with SELinux permissive (boot cmdline patch) because of the stock vendor SELinux policy.",
            "Flash with fastboot as described in FlashCustomImages.md, with an empty vbmeta and verification disabled. This toolkit never flashes anything.",
        },
        SourceReference = "howtos/WorkingWithGSI.md, howtos/FlashCustomImages.md, tools/unpack_gsi_super.sh, tools/repack_gsi_super.sh",
    };

    public static readonly RomProfile Custom = new()
    {
        Id = RomProfileId.CustomSystem,
        Name = "Custom ROM / manually selected system.img",
        AndroidVersion = "Depends on the image",
        DeviceVariant = "X04G with the stock vendor partition",
        Status = DocumentationStatus.NotDocumented,
        StatusText = "Experimental: not documented",
        Summary = "Replace the system partition of your super image with any system.img you choose (for example one you modified yourself). Product is kept unless you choose to remove it.",
        Inputs = new[]
        {
            new RequiredInput(InputKind.SuperImg, "Base super image", SuperDesc, false),
            new RequiredInput(InputKind.SystemImage, "system.img", "The system image to place in super (raw or sparse).", false),
            new RequiredInput(InputKind.BootImage, "boot.bin / boot.img", "Optional, for the SELinux-permissive cmdline patch.", true),
        },
        StorageNotes = "Calculated from your images and the super metadata.",
        Caveats = new[]
        {
            "No procedure for arbitrary system images exists in the original repository; compatibility is entirely up to the image.",
        },
        SourceReference = "(none — extension of WorkingWithGSI.md)",
    };

    public static IReadOnlyList<RomProfile> All { get; } = new[] { X04G, LX04, Gsi, Custom };

    public static RomProfile Get(RomProfileId id) => All.First(p => p.Id == id);
}
