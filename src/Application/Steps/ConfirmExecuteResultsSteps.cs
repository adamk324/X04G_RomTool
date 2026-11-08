using System.Diagnostics;
using System.Windows.Forms;
using X04G.Core.Common;
using X04G.Core.Preparation;

namespace X04G.Toolkit.App.Steps;

internal sealed class ConfirmStep : WizardStep
{
    private readonly Stack _stack = new();
    private CheckBox? _ack;

    public ConfirmStep(WizardState s, MainForm h) : base(s, h) => Controls.Add(_stack);
    public override string Title => "Confirmation";
    public override string NextText => State.DryRun ? "Start dry run" : "Execute";

    public override void OnEnter()
    {
        _stack.Clear();
        _ack = null;
        var plan = State.Plan;
        var r = State.Report;
        _stack.Add(Ui.Heading(State.DryRun ? "Confirm the dry run" : "Confirm"));
        if (plan == null || r == null) { _stack.Add(Ui.Para("Nothing planned.")); return; }
        _stack.Add(Ui.Row(Ui.Badge(r.CapacityVerdict, "Capacity"), Ui.Badge(r.CompatibilityVerdict, "Compatibility")));

        _stack.Add(Ui.Sub("Files used (opened read-only)"));
        foreach (var f in plan.AllReads) _stack.Add(Ui.Para("  " + f));
        _stack.Add(Ui.Sub(State.DryRun ? "Files that WOULD be created (dry run creates nothing)" : "Files and folders that will be created"));
        foreach (var f in plan.AllCreates) _stack.Add(Ui.Para("  " + f));
        var deletes = plan.Operations.SelectMany(o => o.Deletes).ToList();
        _stack.Add(Ui.Sub("Files that will be modified or deleted"));
        _stack.Add(Ui.Para(deletes.Count == 0 ? "  None. No existing file is modified." : string.Join("\n", deletes.Select(d => "  " + d + " (only the toolkit's own working copies, after verification)"))));

        _stack.Add(Ui.Sub("Steps"));
        int i = 1;
        foreach (var op in plan.Operations) _stack.Add(Ui.Para($"{i++}. {op.Description}"));
        _stack.Add(Ui.Para("Nothing is flashed to the device.", Theme.PassFore), 4);

        if (!plan.CanExecute)
        {
            foreach (var p in plan.Problems) _stack.Add(Ui.Status(p, Verdict.Fail));
            _stack.Add(Ui.FindingsBox(new (string, IEnumerable<Finding>)[] { ("Failures", r.InputFindings.Concat(r.CapacityFindings).Where(f => f.Severity == Severity.Fail)) }, 140));
        }
        else if (r.Overall == Verdict.Warning)
        {
            _stack.Add(Ui.FindingsBox(new (string, IEnumerable<Finding>)[] { ("Warnings", r.InputFindings.Concat(r.CapacityFindings).Concat(r.CompatibilityFindings).Where(f => f.Severity == Severity.Warning)) }, 160), 8);
            _ack = _stack.Add(new CheckBox { Text = "I have read the warnings. I understand that the result cannot be fully verified and that a successful build does not mean the image boots.", AutoSize = true, Font = Theme.Bold });
            _ack.CheckedChanged += (_, _) => Host.RefreshButtons();
        }
    }

    public override string? ValidateNext()
    {
        if (State.Plan is not { CanExecute: true }) return "This plan cannot be executed. Go back and change the inputs or options.";
        if (_ack != null && !_ack.Checked) return "Please confirm that you have read the warnings.";
        return null;
    }
}

internal sealed class ExecuteStep : WizardStep
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Height = 20, Dock = DockStyle.Top };
    private readonly Label _stage = Ui.Para("");
    private readonly Label _headline = Ui.Heading("Running");
    private readonly TextBox _log = Ui.LogBox();
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private CancellationTokenSource? _cts;
    private bool _busy;
    private PreparationPlan? _ranPlan;
    private DateTime _lastUi;

    public ExecuteStep(WizardState s, MainForm h) : base(s, h)
    {
        var top = new Stack { Dock = DockStyle.Top, AutoScroll = false, Height = 150 };
        top.Add(_headline);
        top.Add(_stage);
        top.Add(_bar);
        top.Add(Ui.Row(_cancel));
        Controls.Add(_log);
        Controls.Add(top);
        _cancel.Click += (_, _) => { _cts?.Cancel(); _cancel.Enabled = false; _stage.Text = "Cancelling…"; };
    }

    public override string Title => "Execute";
    public override bool IsBusy => _busy;
    public override bool AllowBack => !_busy;
    public override string NextText => "Results >";
    public override bool SkipOnBack => true;

    public override async void OnEnter()
    {
        if (_busy || _ranPlan == State.Plan && State.Result != null) return;
        await RunAsync();
    }

    private async Task RunAsync()
    {
        var plan = State.Plan!;
        _ranPlan = plan;
        State.Result = null;
        _busy = true;
        _cts = new CancellationTokenSource();
        _cancel.Enabled = true;
        _log.Clear();
        _bar.Value = 0;
        _headline.Text = plan.Request.DryRun ? "Dry run" : "Running";
        Host.RefreshButtons();

        var log = State.NewLog();
        void OnEntry(LogEntry e) => BeginInvokeSafe(() => _log.AppendText(e + Environment.NewLine));
        log.EntryAdded += OnEntry;
        var progress = new Progress<ProgressInfo>(p =>
        {
            if ((DateTime.UtcNow - _lastUi).TotalMilliseconds < 80 && p.Fraction < 1) return;
            _lastUi = DateTime.UtcNow;
            _stage.Text = p.Detail != null ? $"{p.Stage} — {p.Detail}" : $"{p.Stage} ({p.Fraction:P0})";
            _bar.Value = (int)Math.Clamp(p.Fraction * 1000, 0, 1000);
        });
        try
        {
            var exec = new PreparationExecutor(log, State.Toolchain, progress);
            var token = _cts.Token;
            State.Result = await Task.Run(() => exec.Execute(plan, token));
            var res = State.Result;
            _bar.Value = res.Success ? 1000 : _bar.Value;
            _headline.Text = res.Success ? (res.DryRun ? "Dry run finished" : "Finished") : "Stopped";
            _stage.Text = res.Success ? "Done. Click Results to see the generated files." : $"Error: {res.Error}";
            _stage.ForeColor = res.Success ? Theme.PassFore : Theme.FailFore;
        }
        finally
        {
            log.EntryAdded -= OnEntry;
            _busy = false;
            _cancel.Enabled = false;
            Host.RefreshButtons();
        }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    public override string? ValidateNext() => State.Result == null ? "The operation has not finished." : null;
}

internal sealed class ResultsStep : WizardStep
{
    private readonly Stack _stack = new();
    public ResultsStep(WizardState s, MainForm h) : base(s, h) => Controls.Add(_stack);
    public override string Title => "Results";
    public override string NextText => "Start over";

    public override void OnEnter()
    {
        _stack.Clear();
        var res = State.Result;
        _stack.Add(Ui.Heading(res?.DryRun == true ? "Dry-run results" : "Results"));
        if (res == null) { _stack.Add(Ui.Para("No results.")); return; }
        _stack.Add(res.Success
            ? Ui.Status(res.DryRun ? "Dry run completed. No files were written." : "Completed successfully.", Verdict.Pass)
            : Ui.Status("Stopped: " + res.Error, Verdict.Fail));
        if (res.Verified == true) _stack.Add(Ui.Status("Verification passed: metadata and SHA-256 of every partition re-read from the output.", Verdict.Pass));
        _stack.Add(Ui.Status("A successful build does not prove the image boots on the device. Nothing was flashed.", Verdict.Warning));

        if (res.Outputs.Count > 0)
        {
            _stack.Add(Ui.Sub("Generated files"));
            var lv = new ListView { View = View.Details, FullRowSelect = true, Height = 40 + 22 * res.Outputs.Count, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            lv.Columns.Add("File", 360); lv.Columns.Add("Size", 110); lv.Columns.Add("Description", 300); lv.Columns.Add("SHA-256", 460);
            foreach (var o in res.Outputs)
                lv.Items.Add(new ListViewItem(new[] { res.RunDirectory != null ? Path.GetRelativePath(res.RunDirectory, o.Path) : o.Path, ByteSize.Format(o.Size), o.Description, o.Sha256 ?? "" }));
            _stack.Add(lv);
        }
        if (res.FinalMetadata != null && State.Report != null)
        {
            _stack.Add(Ui.Sub("Final compatibility report"));
            ReportView.Render(_stack, State.Report, compact: true);
        }

        var buttons = new List<Control>();
        if (res.RunDirectory != null && Directory.Exists(res.RunDirectory))
        {
            var open = new Button { Text = "Open run folder", AutoSize = true };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{res.RunDirectory}\"") { UseShellExecute = true });
            buttons.Add(open);
        }
        var saveLog = new Button { Text = "Save log…", AutoSize = true };
        saveLog.Click += (_, _) => ReportStep.SaveText(this, State.Log.ToText(), "X04G_operation.log");
        var copy = new Button { Text = "Copy report", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(res.FinalReport);
        buttons.Add(saveLog); buttons.Add(copy);
        _stack.Add(Ui.Row(buttons.ToArray()), 8);

        _stack.Add(Ui.Sub("Next steps (manual, not done by this toolkit)"));
        _stack.Add(Ui.Para("Flashing is described in the original FlashCustomImages.md (bootloader unlock with MTKClient, then fastboot flash boot / super / vbmeta with --disable-verity --disable-verification). Only flash after keeping a verified full backup."));
        var report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono, Height = 260, Text = res.FinalReport.Replace("\n", Environment.NewLine) };
        _stack.Add(Ui.Sub("Full report"));
        _stack.Add(report);
    }
}
