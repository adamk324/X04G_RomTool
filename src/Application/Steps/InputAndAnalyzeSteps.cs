using System.Diagnostics;
using System.Windows.Forms;
using X04G.Core.Common;
using X04G.Core.RomProfiles;
using X04G.Core.StorageAnalysis;

namespace X04G.Toolkit.App.Steps;

internal sealed class InputFilesStep : WizardStep
{
    private readonly Stack _stack = new();
    private FilePicker? _source, _system, _boot, _output;
    private readonly Dictionary<string, FilePicker> _extra = new();

    public InputFilesStep(WizardState s, MainForm h) : base(s, h) => Controls.Add(_stack);

    public override string Title => "Input files";

    public override void OnEnter()
    {
        var p = State.Profile!;
        _stack.Clear();
        _extra.Clear();
        _stack.Add(Ui.Heading("Select input files"));
        _stack.Add(Ui.Para($"Target: {p.Name}. Files are only opened read-only, and nothing is processed until you continue."));
        if (p.Id == RomProfileId.LX04Chinese)
            _source = _stack.Add(new FilePicker("LX04 image to analyze", "An LX04 super image (if your backup has one) or a system/other partition image from your own LX04 backup.", true));
        else
            _source = _stack.Add(new FilePicker(p.ReplacesSystem ? "Base super image" : "Super image",
                "super.bin from your MTKClient \"Read partition(s)\" backup, or a super.img produced by this toolkit or the original scripts (sparse or raw)." +
                (p.ReplacesSystem ? " Its vendor partition is kept." : ""), true));
        _source.Filter = "Super images (*.bin;*.img)|*.bin;*.img|All files (*.*)|*.*";
        _source.Path = State.SourcePath;

        if (p.ReplacesSystem)
        {
            _system = _stack.Add(new FilePicker(p.Id == RomProfileId.Gsi ? "GSI system image" : "system.img",
                "Raw or sparse ext4/EROFS system image. Decompress .xz / .gz / .zip / .7z downloads first.", true));
            _system.Path = State.SystemPath;
            _boot = _stack.Add(new FilePicker("boot.bin / boot.img",
                "Your own boot partition backup. Needed only for the documented SELinux-permissive command line patch.", false));
            _boot.Path = State.BootPath;
        }
        else { _system = null; _boot = null; }

        if (p.Id == RomProfileId.X04GGlobal)
        {
            _stack.Add(Ui.Sub("Optional: replace partitions with your own modified images"));
            _stack.Add(Ui.Muted("Equivalent to editing the mounted images with the original scripts and repacking. Leave empty to keep the partition from the super image."));
            foreach (var name in new[] { "system", "vendor", "product" })
            {
                var fp = _stack.Add(new FilePicker($"Replacement {name}.img", $"Modified {name} image (raw or sparse).", false));
                fp.Path = State.ExtraReplacements.GetValueOrDefault(name);
                _extra[name] = fp;
            }
        }

        _output = _stack.Add(new FilePicker("Output folder", "A new, uniquely named run folder is created inside this folder for every run.", true, folder: true));
        _output.Path = State.OutputDirectory;
    }

    public override string? ValidateNext()
    {
        var p = State.Profile!;
        if (_source?.Path is not string src) return "Select the source image.";
        if (!File.Exists(src)) return $"File not found:\n{src}";
        if (p.ReplacesSystem)
        {
            if (_system?.Path is not string sys) return "Select the system image.";
            if (!File.Exists(sys)) return $"File not found:\n{sys}";
            if (SafeFile.IsSameFile(sys, src)) return "The system image and the super image are the same file.";
        }
        if (_boot?.Path is string b && !File.Exists(b)) return $"File not found:\n{b}";
        foreach (var (n, fp) in _extra) if (fp.Path is string x && !File.Exists(x)) return $"Replacement for {n} not found:\n{x}";
        if (_output?.Path is not string o) return "Select an output folder.";
        try { _ = Path.GetFullPath(o); } catch { return "The output folder path is not valid."; }
        if (File.Exists(o)) return "The output path points to a file, not a folder.";
        return null;
    }

    public override Task<bool> OnNextAsync()
    {
        bool hadBoot = State.BootPath != null;
        if (State.SourcePath != _source!.Path || State.SystemPath != _system?.Path ||
            State.ExtraReplacements.GetValueOrDefault("vendor") != _extra.GetValueOrDefault("vendor")?.Path)
        {
            // Different vendor or system: re-evaluate which known fixes are recommended.
            State.EnabledFixes.Clear();
            State.FixDefaultsApplied = false;
        }
        State.SourcePath = _source!.Path;
        State.SystemPath = _system?.Path;
        State.BootPath = _boot?.Path;
        State.ExtraReplacements.Clear();
        foreach (var (n, fp) in _extra) if (fp.Path != null) State.ExtraReplacements[n] = fp.Path;
        State.OutputDirectory = _output!.Path!;
        if (!hadBoot && State.BootPath != null && State.Profile!.OffersBootPatch) State.PatchBoot = true;
        if (State.BootPath == null) State.PatchBoot = false;
        return Task.FromResult(true);
    }
}

/// <summary>Runs the read-only analysis in the background and advances to the report.</summary>
internal sealed class AnalyzeStep : WizardStep
{
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 18, Dock = DockStyle.Top };
    private readonly Label _label = Ui.Para("");
    private readonly TextBox _log = Ui.LogBox();
    private bool _busy;

    public AnalyzeStep(WizardState s, MainForm h) : base(s, h)
    {
        var top = new Stack { Dock = DockStyle.Top, AutoScroll = false, Height = 130 };
        top.Add(Ui.Heading("Analyzing"));
        top.Add(Ui.Para("Reading partition metadata, filesystems and build properties. This is read-only: no file is created or modified."));
        top.Add(_label);
        top.Add(_bar);
        Controls.Add(_log);
        Controls.Add(top);
    }

    public override string Title => "Analyze";
    public override bool IsBusy => _busy;
    public override string NextText => "Show report >";
    public override bool SkipOnBack => true;

    public override async void OnEnter()
    {
        if (_busy) return;
        await RunAsync();
    }

    public async Task RunAsync()
    {
        _busy = true;
        Host.RefreshButtons();
        _log.Clear();
        _bar.Style = ProgressBarStyle.Marquee;
        _label.Text = $"Analyzing {Path.GetFileName(State.SourcePath)} …";
        var log = new OperationLog();
        log.EntryAdded += e => BeginInvokeSafe(() => _log.AppendText(e + Environment.NewLine));
        var sw = Stopwatch.StartNew();
        try
        {
            var req = State.BuildRequest();
            State.Report = await Task.Run(() => StorageAnalyzer.Analyze(req, State.Toolchain, log));
            if (!State.FixDefaultsApplied)
            {
                // Pre-select known fixes that the analysis says this system needs, then re-analyze so the report shows them.
                var recommended = State.Report.KnownFixes.Where(f => f.Recommended && f.Blocker == null).Select(f => f.Id).ToList();
                State.FixDefaultsApplied = true;
                if (recommended.Count > 0)
                {
                    foreach (var id in recommended) State.EnabledFixes.Add(id);
                    log.Info($"Known fix recommended and pre-selected: {string.Join(", ", recommended)} (can be turned off in the options).");
                    var req2 = State.BuildRequest();
                    State.Report = await Task.Run(() => StorageAnalyzer.Analyze(req2, State.Toolchain, log));
                }
            }
            _label.Text = $"Analysis finished in {sw.Elapsed.TotalSeconds:0.0} s.";
            _bar.Style = ProgressBarStyle.Continuous; _bar.Value = _bar.Maximum;
            _busy = false;
            Host.RefreshButtons();
            Host.Advance();
        }
        catch (Exception ex)
        {
            _label.Text = "Analysis failed: " + ex.Message;
            _bar.Style = ProgressBarStyle.Continuous; _bar.Value = 0;
            State.Report = null;
        }
        finally
        {
            _busy = false;
            Host.RefreshButtons();
        }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    public override string? ValidateNext() => State.Report == null ? "The analysis did not complete. Go back and check the input files." : null;
}
