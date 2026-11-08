using System.Drawing;
using System.Text;
using System.Windows.Forms;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Toolkit.App.Steps;

internal static class ReportView
{
    /// <summary>Renders a storage report into a stack (badges, bars, numbers, findings).</summary>
    public static void Render(Stack stack, StorageReport r, bool compact = false)
    {
        stack.Add(Ui.Row(Ui.Badge(r.CapacityVerdict, "Capacity"), Ui.Badge(r.CompatibilityVerdict, "Compatibility"), Ui.Badge(r.Overall, "Overall")));
        stack.Add(Ui.Muted("PASS = fits within verified capacity · WARNING = cannot be fully verified · FAIL = exceeds capacity or invalid input. PASS is never shown when partition information is missing or uncertain."));

        if (r.SourceMetadata is { } md)
        {
            var cur = new StorageBar { Caption = $"Current super layout ({Ui.Size(r.DeviceSize)})", Total = md.SuperDevice.Size, MetadataBytes = md.SuperDevice.FirstLogicalSector * LpFormat.SectorSize };
            foreach (var p in md.Partitions)
            {
                var ex = md.ExtentsOf(p).FirstOrDefault(e => e.TargetType == LpFormat.TargetLinear);
                if (ex != null) cur.Segments.Add((p.Name, ex.TargetData * LpFormat.SectorSize, md.SizeOf(p)));
            }
            stack.Add(cur, 6);
            var layout = r.Layout?.Metadata ?? r.ShrinkLayout?.Metadata;
            if (layout != null)
            {
                var planned = new StorageBar
                {
                    Caption = r.Layout?.Success == true ? "Planned layout (simulated with the lpmake allocator)" : "Planned layout — only if every ext4 image were shrunk to its used data (lower bound)",
                    Total = layout.SuperDevice.Size, MetadataBytes = layout.SuperDevice.FirstLogicalSector * LpFormat.SectorSize,
                };
                foreach (var p in layout.Partitions)
                {
                    var ex = layout.ExtentsOf(p).FirstOrDefault();
                    if (ex != null) planned.Segments.Add((p.Name, ex.TargetData * LpFormat.SectorSize, layout.SizeOf(p)));
                }
                stack.Add(planned);
            }
            else if (r.Planned.Count > 0)
                stack.Add(Ui.Status($"The planned partitions ({Ui.Size(r.RequiredBytes)}) do not fit into {Ui.Size(r.UsableBytes)} of usable space.", Verdict.Fail));
        }

        stack.Add(Ui.Sub("Storage"));
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
        void Row(string k, string v)
        {
            grid.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 2, 24, 2) });
            grid.Controls.Add(new Label { Text = v, AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 2, 0, 2) });
        }
        string S(ulong? v) => v is ulong x ? ByteSize.WithExact(x) : "unknown";
        Row("Total super partition (device storage for ROM)", S(r.DeviceSize));
        Row("Reserved for LP metadata (incl. alignment)", S(r.MetadataReservedBytes));
        Row("Usable for logical partitions", S(r.UsableBytes));
        Row("Currently allocated", S(r.CurrentAllocatedBytes));
        Row("Required by the planned layout", S(r.RequiredBytes));
        if (r.ShrinkRequiredLowerBound is ulong lb) Row("Required if shrunk (lower bound)", S(lb));
        Row("Remaining after the planned layout", S(r.RemainingBytes));
        if (r.EstimatedOutputBytes is long eo) Row("Estimated output image", (r.EstimatedOutputIsUpperBound ? "≤ " : "") + ByteSize.WithExact(eo));
        if (r.HostDisk is { } hd)
            Row($"Windows drive ({Path.GetPathRoot(Path.GetFullPath(hd.Directory))})", $"{(hd.FreeBytes is long f ? ByteSize.Format(f) : "unknown")} free, ~{ByteSize.Format(hd.RequiredBytes)} needed");
        stack.Add(grid);

        if (!compact && (r.SourcePartitions.Count > 0 || r.Planned.Count > 0))
        {
            stack.Add(Ui.Sub("Partitions"));
            var lv = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 30 + 22 * (r.SourcePartitions.Count + r.Planned.Count + r.Removed.Count + 1), Font = Theme.Body };
            lv.Columns.Add("Partition", 110);
            lv.Columns.Add("Now", 120);
            lv.Columns.Add("Planned", 120);
            lv.Columns.Add("Source of new content", 260);
            lv.Columns.Add("Filesystem / properties", 420);
            foreach (var sp in r.SourcePartitions)
            {
                var pl = r.Planned.FirstOrDefault(p => p.Name == sp.Name);
                var it = new ListViewItem(new[]
                {
                    sp.Name, ByteSize.Format(sp.Size),
                    pl != null ? ByteSize.Format(pl.AlignedSize) : r.Removed.Contains(sp.Name) ? "removed" : "—",
                    pl == null ? "" : pl.IsReplacement ? Path.GetFileName(pl.Source) : "unchanged",
                    pl?.Image != null && pl.IsReplacement ? StorageReport.Describe(pl.Image) : sp.Image != null ? StorageReport.Describe(sp.Image) : "",
                });
                if (r.Removed.Contains(sp.Name)) it.ForeColor = Theme.Muted;
                lv.Items.Add(it);
            }
            stack.Add(lv);
        }

        stack.Add(Ui.Sub("Checks"));
        stack.Add(Ui.FindingsBox(new (string, IEnumerable<Finding>)[]
        {
            ("Input", r.InputFindings), ("Capacity", r.CapacityFindings), ("Compatibility", r.CompatibilityFindings),
        }, compact ? 180 : 280));
    }
}

internal sealed class ReportStep : WizardStep
{
    private readonly Stack _stack = new();
    public ReportStep(WizardState s, MainForm h) : base(s, h) => Controls.Add(_stack);
    public override string Title => "Compatibility report";
    public override string NextText => "Choose operations >";

    public override void OnEnter()
    {
        _stack.Clear();
        var r = State.Report;
        _stack.Add(Ui.Heading("Compatibility report"));
        if (r == null) { _stack.Add(Ui.Para("No report available.")); return; }
        _stack.Add(Ui.Para($"{r.Profile.Name} · {Path.GetFileName(r.SourcePath)} ({(r.SourceIsSparse ? "sparse" : "raw")}, {ByteSize.Format(r.SourceFileLength)}). Nothing has been written."));
        ReportView.Render(_stack, r);
        var save = new Button { Text = "Save report…", AutoSize = true };
        var copy = new Button { Text = "Copy to clipboard", AutoSize = true };
        save.Click += (_, _) => SaveText(this, r.ToText(), "X04G_report.txt");
        copy.Click += (_, _) => Clipboard.SetText(r.ToText());
        _stack.Add(Ui.Row(save, copy), 6);
        if (r.BlocksExecution)
            _stack.Add(Ui.Status("Building is blocked by a FAIL. You can still review options — e.g. removing a partition or enabling shrinking may change the result.", Verdict.Fail));
    }

    public static void SaveText(IWin32Window owner, string text, string name)
    {
        using var d = new SaveFileDialog { FileName = name, Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", OverwritePrompt = true };
        if (d.ShowDialog(owner) == DialogResult.OK) File.WriteAllText(d.FileName, text, new UTF8Encoding(false));
    }
}

internal sealed class OptionsStep : WizardStep
{
    private readonly Stack _stack = new();
    private CheckBox _extract = null!, _build = null!, _removeProduct = null!, _shrink = null!, _verify = null!, _patchBoot = null!, _cleanup = null!, _dry = null!;
    private RadioButton _sparse = null!, _raw = null!;

    public OptionsStep(WizardState s, MainForm h) : base(s, h) => Controls.Add(_stack);
    public override string Title => "Preparation options";
    public override string NextText => "Review >";

    private CheckBox Option(string text, string help, bool value, bool enabled = true)
    {
        var cb = new CheckBox { Text = text, Checked = value && enabled, Enabled = enabled, AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 8, 0, 0) };
        _stack.Add(cb);
        _stack.Add(new Label { Text = help, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(20, 0, 0, 2) });
        return cb;
    }

    public override void OnEnter()
    {
        var p = State.Profile!;
        var r = State.Report;
        bool super = r?.SourceIsSuper == true;
        bool lx = p.Id == RomProfileId.LX04Chinese;
        _stack.Clear();
        _stack.Add(Ui.Heading("Choose the operations"));
        _stack.Add(Ui.Para("Nothing runs until you confirm on the next page. All outputs are new files inside a new run folder."));

        _stack.Add(Ui.Sub("Unpack"));
        _extract = Option("Extract all partitions to separate .img files", "lpunpack equivalent (unpack_bin_super.sh / unpack_img_super.sh): writes extracted\\system.img, vendor.img, product.img … You can edit those (e.g. in WSL) and later select them as replacements.", State.ExtractPartitions, super);

        _stack.Add(Ui.Sub("Build"));
        _build = Option("Build a new super image", lx ? "Not available for LX04 (analysis only)." :
            p.ReplacesSystem ? "Keeps vendor from your super, uses the selected image as system (repack_gsi_super.sh equivalent)." :
                               "Repacks system, vendor and product (repack_super_from_bin.sh / _from_img.sh equivalent), with any replacements you selected.", State.BuildSuper, super && !lx);
        _removeProduct = Option("Remove the product partition", p.Id == RomProfileId.Gsi
            ? "Documented for GSIs (WorkingWithGSI.md): the GSI is larger than the stock system partition and product is not needed, so it is dropped to make room."
            : "Not part of the documented stock workflow. Only remove product if you know the system image does not need it.", State.RemoveProduct, super && !lx && (r?.SourcePartitions.Any(x => x.Name == "product") ?? false));
        var fmt = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(20, 6, 0, 0) };
        _sparse = new RadioButton { Text = "Sparse image (recommended; what fastboot flash super expects; same as the original scripts)", AutoSize = true, Checked = State.Format == OutputFormat.Sparse };
        _raw = new RadioButton { Text = "Raw image (full super size, same layout as an MTKClient super.bin)", AutoSize = true, Checked = State.Format == OutputFormat.Raw };
        fmt.FlowDirection = FlowDirection.TopDown;
        fmt.Controls.AddRange(new Control[] { _sparse, _raw });
        _stack.Add(fmt);
        _verify = Option("Verify the new image after writing", "Re-reads the output, checks the metadata and compares the SHA-256 of every partition with what was written.", State.Verify, super && !lx);

        _stack.Add(Ui.Sub("Optional optimization"));
        bool tc = State.Toolchain != null;
        _shrink = Option("Shrink ext4 images to their minimum size (e2fsck -fy, resize2fs -M)",
            (tc ? $"Uses {State.Toolchain!.Name}. " : "Unavailable: no ext4 tools found (install WSL + e2fsprogs, or put e2fsprogs in tools\\e2fsprogs). ") +
            "This is what the original repack scripts always did. It runs on working copies in the run folder (your originals are never touched, so it is fully reversible), " +
            "removes only unused free space inside the filesystem, and leaves the partition with no free space. Files, drivers and libraries are never removed. EROFS and ext4 images with shared_blocks cannot be shrunk.",
            State.ShrinkExt4, tc && super && !lx);
        _cleanup = Option("Delete the working copies after a verified build", "Only affects the shrink working copies in the run folder's work\\ subfolder. Off by default: intermediate files are kept.", State.Cleanup, tc && super && !lx);

        if (p.OffersBootPatch)
        {
            _stack.Add(Ui.Sub("Boot image"));
            _patchBoot = Option("Create a boot image with SELinux permissive", State.BootPath == null
                ? "Select a boot.bin on the Input files page to enable this."
                : $"Documented in WorkingWithGSI.md: replaces \"buildvariant=user\" with \"buildvariant=userdebug enforcing=0 androidboot.selinux=permissive\" in a copy of {Path.GetFileName(State.BootPath)}. Kernel and ramdisk stay byte-identical.",
                State.PatchBoot, State.BootPath != null);
        }
        else _patchBoot = new CheckBox { Checked = false };

        _stack.Add(Ui.Sub("Mode"));
        _dry = Option("Dry run (validate and simulate only — write nothing)", "Reads every input completely, simulates the build in memory and reports the exact output size, but creates no files or folders.", State.DryRun);
    }

    public override string? ValidateNext()
    {
        if (!_extract.Checked && !_build.Checked && !_patchBoot.Checked) return "Select at least one operation.";
        return null;
    }

    public override async Task<bool> OnNextAsync()
    {
        State.ExtractPartitions = _extract.Checked;
        State.BuildSuper = _build.Checked;
        State.RemoveProduct = _removeProduct.Checked;
        State.Format = _raw.Checked ? OutputFormat.Raw : OutputFormat.Sparse;
        State.Verify = _verify.Checked;
        State.ShrinkExt4 = _shrink.Checked;
        State.Cleanup = _cleanup.Checked;
        State.PatchBoot = _patchBoot.Checked;
        State.DryRun = _dry.Checked;

        // Options change the layout and disk needs: re-analyze (read-only) before confirmation.
        Host.SetStatus("Updating the analysis for the selected options…");
        var req = State.BuildRequest();
        State.Report = await Task.Run(() => StorageAnalyzer.Analyze(req, State.Toolchain));
        State.Plan = PreparationPlanner.Plan(req, State.Report, State.Toolchain != null);
        Host.SetStatus("");
        return true;
    }
}
