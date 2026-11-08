using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Toolkit.App;

public enum DeviceVariant { X04G, LX04 }
public enum RomCategory { Stock, Gsi, Custom }

/// <summary>Everything the user chose in the wizard plus the results of analysis/execution.</summary>
public sealed class WizardState
{
    public DeviceVariant Device { get; set; } = DeviceVariant.X04G;
    public RomCategory Category { get; set; } = RomCategory.Stock;
    public RomProfile? Profile { get; set; }

    public string? SourcePath { get; set; }
    public string? SystemPath { get; set; }
    public string? BootPath { get; set; }
    /// <summary>X04G stock profile: optional replacements for system/vendor/product (e.g. images you modified).</summary>
    public Dictionary<string, string> ExtraReplacements { get; } = new();
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "X04G Toolkit");

    // Options (step 6)
    public bool ExtractPartitions { get; set; }
    public bool BuildSuper { get; set; } = true;
    public bool RemoveProduct { get; set; }
    public bool ShrinkExt4 { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Sparse;
    public bool Verify { get; set; } = true;
    public bool PatchBoot { get; set; }
    public bool Cleanup { get; set; }
    public bool DryRun { get; set; }

    // Environment
    public IExt4Toolchain? Toolchain { get; set; }
    public List<string> ToolchainNotes { get; } = new();
    public bool ToolchainProbed { get; set; }

    // Results
    public StorageReport? Report { get; set; }
    public PreparationPlan? Plan { get; set; }
    public ExecutionResult? Result { get; set; }
    public OperationLog Log { get; private set; } = new();

    public void ApplyProfileDefaults()
    {
        if (Profile == null) return;
        RemoveProduct = Profile.RemovesProductByDefault;
        BuildSuper = Profile.Id != RomProfileId.LX04Chinese;
        ExtractPartitions = false;
        PatchBoot = Profile.OffersBootPatch && BootPath != null;
        ShrinkExt4 = false;
        DryRun = false;
    }

    /// <summary>Fresh log for a new execution (each run folder gets only its own entries).</summary>
    public OperationLog NewLog()
    {
        Log.Dispose();
        Log = new OperationLog();
        return Log;
    }

    public void ResetResults()
    {
        Report = null; Plan = null; Result = null;
        Log.Dispose();
        Log = new OperationLog();
    }

    public PreparationRequest BuildRequest()
    {
        if (Profile == null || SourcePath == null) throw new InvalidOperationException("Profile and source must be selected.");
        var req = new PreparationRequest
        {
            Profile = Profile,
            SourcePath = SourcePath,
            OutputDirectory = OutputDirectory,
            ExtractPartitions = ExtractPartitions,
            BuildSuper = BuildSuper && Profile.Id != RomProfileId.LX04Chinese,
            ShrinkExt4 = ShrinkExt4,
            Format = Format,
            VerifyOutput = Verify,
            BootImagePath = Profile.OffersBootPatch ? BootPath : null,
            PatchBoot = PatchBoot && Profile.OffersBootPatch && BootPath != null,
            CleanupIntermediates = Cleanup,
            DryRun = DryRun,
        };
        if (Profile.ReplacesSystem && SystemPath != null) req.Replacements["system"] = SystemPath;
        if (Profile.Id == RomProfileId.X04GGlobal)
            foreach (var (k, v) in ExtraReplacements) req.Replacements[k] = v;
        if (RemoveProduct) req.RemovePartitions.Add("product");
        return req;
    }
}
