using X04G.Core.RomProfiles;

namespace X04G.Core.Preparation;

public enum OutputFormat
{
    /// <summary>Android sparse image, like the original scripts' lpmake --sparse (what fastboot flash super expects).</summary>
    Sparse,
    /// <summary>Raw image of the full super size (same layout as an MTKClient super.bin dump).</summary>
    Raw,
}

/// <summary>Everything the user selected in the wizard. Analysis and execution both read from this.</summary>
public sealed class PreparationRequest
{
    public required RomProfile Profile { get; init; }

    /// <summary>super.bin (MTKClient dump) or super.img (sparse or raw). For LX04: any image to analyze.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Partition name → replacement image path (raw or sparse). For GSI/custom: "system" → the selected image.</summary>
    public Dictionary<string, string> Replacements { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Partitions of the source super that are left out of the new super (GSI: "product").</summary>
    public HashSet<string> RemovePartitions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Write every source partition to its own .img file (lpunpack equivalent).</summary>
    public bool ExtractPartitions { get; set; }

    /// <summary>Build a new super image.</summary>
    public bool BuildSuper { get; set; }

    /// <summary>Optional: shrink ext4 images to their minimum size (e2fsck + resize2fs -M), like the original repack scripts. Runs on copies; needs an ext4 toolchain.</summary>
    public bool ShrinkExt4 { get; set; }

    public OutputFormat Format { get; set; } = OutputFormat.Sparse;

    /// <summary>Re-open the generated super image and verify metadata and the SHA-256 of every partition.</summary>
    public bool VerifyOutput { get; set; } = true;

    public string? BootImagePath { get; set; }

    /// <summary>Apply the documented SELinux-permissive cmdline change to a copy of the boot image.</summary>
    public bool PatchBoot { get; set; }

    /// <summary>Parent folder; a new uniquely named run folder is created inside it.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Delete the work/ folder (shrink working copies) after a successful, verified build.</summary>
    public bool CleanupIntermediates { get; set; }

    public bool DryRun { get; set; }
}
