using X04G.Core.Common;
using X04G.Core.ImageProcessing;

namespace X04G.Core.RomProfiles;

/// <summary>
/// Checks a replacement system image (GSI or custom) against the device's own vendor image and
/// stock system image, using only properties read from the files. It never concludes that an image
/// will boot: the best possible outcome is a list of individual PASS checks plus a WARNING that
/// booting cannot be verified offline.
/// </summary>
public static class SystemCompatibilityChecker
{
    private const string Cat = "Compatibility";

    public static List<Finding> Check(RomProfile profile, AndroidImageInfo newSystem, AndroidImageInfo? vendor, AndroidImageInfo? stockSystem, string? archiveKind = null)
    {
        var f = new List<Finding>();
        void Add(Severity s, string m) => f.Add(new Finding(s, Cat, m));

        if (archiveKind != null)
        {
            Add(Severity.Fail, $"The selected system image is a {archiveKind} archive. Extract it first and select the .img inside.");
            return f;
        }

        // Filesystem
        switch (newSystem.FileSystem)
        {
            case FileSystemKind.Ext4:
                Add(Severity.Pass, $"System image is ext4 ({string.Join(", ", newSystem.Ext4Features)}).");
                break;
            case FileSystemKind.Erofs:
                Add(Severity.Warning, "System image is EROFS. Its contents cannot be inspected here, and whether the stock X04G kernel supports EROFS is not documented. Prefer an ext4 GSI.");
                break;
            case FileSystemKind.Empty:
            case FileSystemKind.Unknown:
                Add(Severity.Fail, "The selected file is not a recognizable system image (no ext4/EROFS superblock).");
                return f;
            default:
                Add(Severity.Fail, $"{newSystem.FileSystem} system images are not used by Android.");
                return f;
        }
        foreach (var n in newSystem.Notes.Where(n => n.Contains("truncated")))
            Add(Severity.Fail, n);

        if (newSystem.FileSystem != FileSystemKind.Ext4)
        {
            Add(Severity.Warning, "Architecture, Android version and VNDK could not be checked.");
            AddAlways(profile, f);
            return f;
        }

        if (newSystem.BuildPropPath == null)
            Add(Severity.Warning, "No build.prop found in the system image; architecture and Android version could not be checked.");
        else
            Add(Severity.Info, $"System image: Android {newSystem.Release ?? "?"} (SDK {newSystem.Sdk?.ToString() ?? "?"}), ABIs {newSystem.AbiList ?? "?"}{(newSystem.Description != null ? $", build {newSystem.Description}" : "")}.");

        // Layout
        if (newSystem.SystemAsRoot is bool gsiSar)
        {
            if (stockSystem?.SystemAsRoot is bool stockSar)
            {
                if (stockSar == gsiSar) Add(Severity.Pass, $"Image layout matches your stock system ({(gsiSar ? "system-as-root" : "non system-as-root")}).");
                else Add(Severity.Fail, $"Image layout ({(gsiSar ? "system-as-root" : "non system-as-root")}) differs from your stock system image ({(stockSar ? "system-as-root" : "non system-as-root")}). Choose the matching GSI variant.");
            }
            else Add(Severity.Warning, $"Image is {(gsiSar ? "system-as-root" : "non system-as-root")}, but the stock system layout is unknown (no stock system in the base super) so it could not be compared.");
        }
        else Add(Severity.Warning, "Could not determine the system image layout.");

        // Architecture
        var abis = newSystem.AbiList ?? "";
        if (abis.Length > 0 && !abis.Contains("arm"))
            Add(Severity.Fail, $"System image ABIs '{abis}' are not ARM. The MT8167 is an ARM (Cortex-A35) SoC.");
        else if (vendor?.AbiList is string vAbi && abis.Length > 0)
        {
            bool v64 = vendor.Supports64, g64 = newSystem.Supports64;
            if (v64 == g64) Add(Severity.Pass, $"Architecture matches the vendor partition ({(g64 ? "64-bit capable" : "32-bit")}: system '{abis}', vendor '{vAbi}').");
            else if (g64 && !v64) Add(Severity.Warning, $"System image is 64-bit ('{abis}') but your vendor reports a 32-bit-only ABI list ('{vAbi}'). A 32-bit (arm / a64) GSI variant is the usual match for 32-bit vendors.");
            else Add(Severity.Warning, $"System image is 32-bit ('{abis}') while your vendor reports '{vAbi}'.");
        }
        else Add(Severity.Warning, "Architecture could not be compared with the vendor partition (missing ABI properties).");

        // Android version vs vendor
        var vSdk = vendor?.Sdk;
        if (newSystem.Sdk is int g && vSdk is int v)
        {
            if (g < v) Add(Severity.Fail, $"System image is older (SDK {g}) than your vendor (SDK {v}). Project Treble does not support an older system on a newer vendor.");
            else if (g == v) Add(Severity.Pass, $"System image targets the same Android release as your vendor (SDK {v}).");
            else Add(Severity.Warning, $"System image (SDK {g}) is newer than your vendor (SDK {v}). This only works if the image still supports Android {vendor!.Release ?? v.ToString()} vendors.");
        }
        else Add(Severity.Warning, "Android version of system or vendor is unknown; version compatibility was not checked.");

        // VNDK
        var vVndk = vendor?.VndkVersion;
        if (vVndk != null)
        {
            bool provided = newSystem.VndkProvided.Contains(vVndk) ||
                            (newSystem.Sdk is int gs && vSdk is int vs && gs == vs && newSystem.VndkProvided.Contains("current"));
            if (provided) Add(Severity.Pass, $"System image ships the VNDK v{vVndk} libraries your vendor requires.");
            else if (newSystem.VndkProvided.Count > 0) Add(Severity.Warning, $"Your vendor requires VNDK v{vVndk}; the system image ships only: {string.Join(", ", newSystem.VndkProvided)}.");
            else Add(Severity.Warning, $"Your vendor requires VNDK v{vVndk}; no VNDK snapshot was found in the system image (it may be stored in a location the toolkit does not check).");
        }
        else Add(Severity.Warning, "Vendor VNDK version (ro.vndk.version) is unknown; VNDK compatibility was not checked.");

        // Treble
        var treble = vendor?.TrebleEnabled;
        if (treble == "true") Add(Severity.Pass, "Vendor declares ro.treble.enabled=true.");
        else if (treble == "false") Add(Severity.Fail, "Vendor declares ro.treble.enabled=false; GSIs are not supported.");
        else if (vendor != null) Add(Severity.Warning, "ro.treble.enabled not found in vendor build.prop.");

        if (newSystem.Ext4SharedBlocks)
            Add(Severity.Info, "System image uses ext4 shared_blocks (deduplication). It can be used as-is but cannot be shrunk with resize2fs.");

        AddAlways(profile, f);
        return f;
    }

    private static void AddAlways(RomProfile profile, List<Finding> f)
    {
        if (profile.Id == RomProfileId.Gsi)
            f.Add(new Finding(Severity.Warning, Cat, "The repository reports that GSIs only boot with SELinux permissive (boot cmdline patch), because of the stock vendor SELinux policy."));
        f.Add(new Finding(Severity.Warning, Cat, "Booting cannot be verified offline. A successful build does not mean the image works on the device."));
    }
}
