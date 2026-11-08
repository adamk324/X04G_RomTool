using System.Drawing;
using System.Windows.Forms;
using X04G.Core.RomProfiles;

namespace X04G.Toolkit.App.Steps;

internal sealed class WelcomeStep : WizardStep
{
    private readonly Stack _stack = new();
    private readonly RadioButton _x04g = new() { Text = "X04G — global unit (Google Assistant, Android Go 10)", AutoSize = true, Checked = true };
    private readonly RadioButton _lx04 = new() { Text = "LX04 — Chinese unit (Xiao AI, Android 8.1)", AutoSize = true };
    private readonly RadioButton _stock = new() { Text = "Stock ROM (unpack / repack / modify the super partition)", AutoSize = true, Checked = true };
    private readonly RadioButton _gsi = new() { Text = "Generic System Image (GSI)", AutoSize = true };
    private readonly RadioButton _custom = new() { Text = "Custom ROM / my own system.img", AutoSize = true };
    private readonly Label _tools = Ui.Muted("Checking for optional ext4 tools…");

    public WelcomeStep(WizardState s, MainForm h) : base(s, h)
    {
        Controls.Add(_stack);
        _stack.Add(Ui.Heading("Welcome"));
        _stack.Add(Ui.Para("This toolkit prepares and analyzes ROM images for the Xiaomi Mi Smart Clock (X04G / LX04, MediaTek MT8167). " +
                           "It replaces the Linux scripts of the x04g_tools repository with native Windows code: no WSL, Linux tools or administrator rights are needed for analysis, unpacking or repacking."));

        _stack.Add(Ui.Sub("Target device"));
        var dev = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        dev.Controls.AddRange(new Control[] { _x04g, _lx04 });
        _stack.Add(dev);
        _stack.Add(Ui.Muted("According to the original repository both variants share the same hardware (MT8167, 1 GB RAM, 4\" 800x480, ~4 GB flash), and either variant's software can be installed."));

        _stack.Add(Ui.Sub("ROM type"));
        var rom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        rom.Controls.AddRange(new Control[] { _stock, _gsi, _custom });
        _stack.Add(rom);

        _stack.Add(Ui.Sub("Safety rules this toolkit follows"));
        foreach (var line in new[]
        {
            "• Your backup files are only ever opened read-only. Nothing existing is overwritten; every run writes to a new folder.",
            "• Nothing starts automatically when you select a file. You confirm the exact list of files before anything is written.",
            "• A dry-run mode validates and simulates everything without writing a single file.",
            "• Nothing is ever flashed to the device. Flashing stays a manual step (see the original FlashCustomImages guide).",
            "• A successful build only proves the image is structurally valid — not that it boots.",
        }) _stack.Add(Ui.Para(line));

        _stack.Add(Ui.Sub("Before you start"));
        _stack.Add(Ui.Para("You need your own full MTKClient backup (\"Read partition(s)\" → super.bin, boot.bin …). There is no official X04G firmware download, so that backup is the only way back to stock. Keep it untouched and do not share it — it contains device-unique IDs."));
        _stack.Add(Ui.Sub("Optional ext4 tools"));
        _stack.Add(_tools);
    }

    public override string Title => "Welcome";

    public override void OnEnter()
    {
        _x04g.Checked = State.Device == DeviceVariant.X04G;
        _lx04.Checked = State.Device == DeviceVariant.LX04;
        _stock.Checked = State.Category == RomCategory.Stock;
        _gsi.Checked = State.Category == RomCategory.Gsi;
        _custom.Checked = State.Category == RomCategory.Custom;
        if (!State.ToolchainProbed) _tools.Text = "Checking for optional ext4 tools…";
        else if (State.Toolchain != null) _tools.Text = $"Found: {State.Toolchain.Name}. Shrinking ext4 images (resize2fs -M, as the original repack scripts did) is available as an option.";
        else _tools.Text = "Not found. Everything works without them except the optional \"shrink ext4 images\" step. To enable it, install WSL with e2fsprogs (wsl --install, then sudo apt install e2fsprogs) or place e2fsck.exe/resize2fs.exe in tools\\e2fsprogs next to the program." +
                           (State.ToolchainNotes.Count > 0 ? "\nDetails: " + string.Join(" | ", State.ToolchainNotes) : "");
    }

    public override Task<bool> OnNextAsync()
    {
        State.Device = _lx04.Checked ? DeviceVariant.LX04 : DeviceVariant.X04G;
        State.Category = _gsi.Checked ? RomCategory.Gsi : _custom.Checked ? RomCategory.Custom : RomCategory.Stock;
        var suggested = State.Category switch
        {
            RomCategory.Gsi => RomProfileCatalog.Gsi,
            RomCategory.Custom => RomProfileCatalog.Custom,
            _ => State.Device == DeviceVariant.LX04 ? RomProfileCatalog.LX04 : RomProfileCatalog.X04G,
        };
        if (State.Profile != suggested) { State.Profile = suggested; State.ApplyProfileDefaults(); }
        return Task.FromResult(true);
    }
}

/// <summary>The mandatory ROM selection screen shown before any processing.</summary>
internal sealed class SelectOsStep : WizardStep
{
    private readonly Stack _stack = new();
    private readonly List<(RomProfile Profile, RadioButton Radio, TableLayoutPanel Card)> _cards = new();
    private readonly Label _caveats = Ui.Para("");

    public SelectOsStep(WizardState s, MainForm h) : base(s, h)
    {
        Controls.Add(_stack);
        _stack.Add(Ui.Heading("Select the operating system"));
        _stack.Add(Ui.Para("Choose what the prepared image should contain. The status shows whether the procedure is documented in the original x04g_tools repository. No file is read until the analysis step."));
        foreach (var p in RomProfileCatalog.All) _stack.Add(MakeCard(p));
        _stack.Add(Ui.Sub("Notes for the selected option"));
        _stack.Add(_caveats);
    }

    public override string Title => "Select operating system";

    private TableLayoutPanel MakeCard(RomProfile p)
    {
        var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 4, 0, 4), BackColor = Color.White };
        var card = inner;
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var radio = new RadioButton { Text = p.Name, Font = Theme.Subtitle, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var (b, f) = p.Status switch
        {
            DocumentationStatus.Documented => (Theme.PassBack, Theme.PassFore),
            DocumentationStatus.DocumentedExperimental => (Theme.WarnBack, Theme.WarnFore),
            _ => (Color.FromArgb(230, 230, 230), Color.FromArgb(70, 70, 70)),
        };
        var badge = new Label { Text = p.Status switch { DocumentationStatus.Documented => "DOCUMENTED", DocumentationStatus.DocumentedExperimental => "EXPERIMENTAL", _ => "NOT DOCUMENTED" }, BackColor = b, ForeColor = f, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Font = Theme.Bold, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        inner.Controls.Add(radio, 0, 0);
        inner.Controls.Add(badge, 1, 0);
        var details =
            $"Android version: {p.AndroidVersion}\n" +
            $"Target: {p.DeviceVariant}\n" +
            $"Status: {p.StatusText}\n" +
            $"Required input: {string.Join("; ", p.Inputs.Select(i => i.Label + (i.Optional ? " (optional)" : "")))}\n" +
            $"Storage: {p.StorageNotes}\n\n{p.Summary}";
        var lbl = new Label { Text = details, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.FromArgb(50, 50, 50), Margin = new Padding(20, 0, 0, 0) };
        inner.Controls.Add(lbl, 0, 1);
        inner.SetColumnSpan(lbl, 2);
        void Select() { radio.Checked = true; }
        card.Click += (_, _) => Select();
        lbl.Click += (_, _) => Select();
        radio.CheckedChanged += (_, _) =>
        {
            if (!radio.Checked) { card.BackColor = Color.White; return; }
            card.BackColor = Color.FromArgb(235, 244, 252);
            foreach (var c in _cards) if (c.Radio != radio) c.Radio.Checked = false;
            _caveats.Text = string.Join("\n", p.Caveats.Select(c => "• " + c)) + $"\n\nSource in the original repository: {p.SourceReference}";
        };
        _cards.Add((p, radio, card));
        return card;
    }

    public override void OnEnter()
    {
        var cur = State.Profile ?? RomProfileCatalog.X04G;
        foreach (var c in _cards) c.Radio.Checked = c.Profile.Id == cur.Id;
    }

    public override string? ValidateNext() => _cards.Any(c => c.Radio.Checked) ? null : "Select an operating system.";

    public override Task<bool> OnNextAsync()
    {
        var chosen = _cards.First(c => c.Radio.Checked).Profile;
        if (State.Profile?.Id != chosen.Id)
        {
            State.Profile = chosen;
            State.ApplyProfileDefaults();
            State.ResetResults();
        }
        return Task.FromResult(true);
    }
}
