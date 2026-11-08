using System.Drawing;
using System.Windows.Forms;
using X04G.Core.ImageProcessing;
using X04G.Toolkit.App.Steps;

namespace X04G.Toolkit.App;

/// <summary>Base class of a wizard page.</summary>
internal abstract class WizardStep : UserControl
{
    protected WizardStep(WizardState state, MainForm host)
    {
        State = state; Host = host;
        Dock = DockStyle.Fill;
        Padding = new Padding(4);
        BackColor = Color.White;
    }

    protected WizardState State { get; }
    protected MainForm Host { get; }
    public abstract string Title { get; }
    /// <summary>Called each time the page is shown.</summary>
    public virtual void OnEnter() { }
    /// <summary>Returns null if Next may proceed, otherwise the reason shown to the user.</summary>
    public virtual string? ValidateNext() => null;
    /// <summary>Called when Next is clicked and validation passed. Return false to stay.</summary>
    public virtual Task<bool> OnNextAsync() => Task.FromResult(true);
    public virtual string NextText => "Next >";
    public virtual bool AllowBack => true;
    public virtual bool IsBusy => false;
    /// <summary>Pages that only run something (analysis, execution) are skipped when going Back.</summary>
    public virtual bool SkipOnBack => false;
}

internal sealed class MainForm : Form
{
    private readonly WizardState _state = new();
    private readonly List<WizardStep> _steps;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 8), BackColor = Color.White };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(10, 16, 6, 6) };
    private readonly Button _back = new() { Text = "< Back", Width = 96, Height = 30 };
    private readonly Button _next = new() { Text = "Next >", Width = 130, Height = 30 };
    private readonly Button _close = new() { Text = "Close", Width = 96, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left };
    private readonly List<Label> _navLabels = new();
    private int _index;

    public MainForm()
    {
        Text = "X04G Windows ROM Toolkit — Xiaomi Mi Smart Clock (X04G / LX04)";
        MinimumSize = new Size(980, 680);
        Size = new Size(1120, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        BackColor = Color.White;
        Icon = SystemIcons.Application;

        _steps = new List<WizardStep>
        {
            new WelcomeStep(_state, this),
            new SelectOsStep(_state, this),
            new InputFilesStep(_state, this),
            new AnalyzeStep(_state, this),
            new ReportStep(_state, this),
            new OptionsStep(_state, this),
            new ConfirmStep(_state, this),
            new ExecuteStep(_state, this),
            new ResultsStep(_state, this),
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var brand = new Label { Text = "X04G ROM Toolkit", Font = Theme.Subtitle, AutoSize = true, ForeColor = Theme.Accent, Margin = new Padding(4, 0, 0, 2) };
        var brandSub = new Label { Text = "Image preparation & storage analysis\nNever flashes the device", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(4, 0, 0, 16) };
        _nav.Controls.Add(brand);
        _nav.Controls.Add(brandSub);
        for (int i = 0; i < _steps.Count; i++)
        {
            var l = new Label { Text = $"{i + 1}.  {_steps[i].Title}", AutoSize = true, Margin = new Padding(4, 5, 0, 5), ForeColor = Theme.Muted };
            _navLabels.Add(l);
            _nav.Controls.Add(l);
        }

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(16, 10, 16, 10), BackColor = Theme.Panel };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_status, 0, 0);
        buttons.Controls.Add(_back, 1, 0);
        buttons.Controls.Add(_next, 2, 0);
        buttons.Controls.Add(_close, 3, 0);
        _close.Margin = new Padding(16, 3, 3, 3);

        root.Controls.Add(_nav, 0, 0);
        root.SetRowSpan(_nav, 2);
        root.Controls.Add(_content, 1, 0);
        root.Controls.Add(buttons, 1, 1);
        Controls.Add(root);

        _back.Click += (_, _) =>
        {
            int i = _index - 1;
            while (i > 0 && _steps[i].SkipOnBack) i--;
            GoTo(i);
        };
        _next.Click += async (_, _) => await NextAsync();
        _close.Click += (_, _) => Close();
        AcceptButton = _next;

        Load += async (_, _) =>
        {
            Show(0);
            await ProbeToolchainAsync();
        };
        FormClosing += (_, e) =>
        {
            if (_steps[_index].IsBusy &&
                MessageBox.Show(this, "An operation is running. Cancel it and close?", "X04G Toolkit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
        };
    }

    public async Task ProbeToolchainAsync()
    {
        SetStatus("Looking for optional ext4 tools (WSL / e2fsprogs)…");
        var (tc, notes) = await Task.Run(() => Ext4ToolchainLocator.Locate(AppContext.BaseDirectory));
        _state.Toolchain = tc;
        _state.ToolchainNotes.Clear();
        _state.ToolchainNotes.AddRange(notes);
        _state.ToolchainProbed = true;
        SetStatus(tc != null ? $"ext4 tools: {tc.Name}" : "ext4 tools: not found (optional — only needed for shrinking)");
        if (_steps[_index] is WelcomeStep) _steps[_index].OnEnter();
    }

    public void SetStatus(string text) => _status.Text = text;

    public void RefreshButtons()
    {
        var step = _steps[_index];
        _back.Enabled = _index > 0 && step.AllowBack && !step.IsBusy;
        _next.Text = step.NextText;
        _next.Enabled = !step.IsBusy && _index < _steps.Count - 1 || step is ResultsStep;
        _close.Enabled = true;
    }

    private void Show(int index)
    {
        _index = index;
        _content.SuspendLayout();
        _content.Controls.Clear();
        var step = _steps[index];
        _content.Controls.Add(step);
        _content.ResumeLayout();
        for (int i = 0; i < _navLabels.Count; i++)
        {
            _navLabels[i].Font = i == index ? Theme.Bold : Theme.Body;
            _navLabels[i].ForeColor = i == index ? Theme.Accent : i < index ? Color.FromArgb(40, 40, 40) : Theme.Muted;
        }
        step.OnEnter();
        RefreshButtons();
    }

    public void GoTo(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        Show(index);
    }

    public void GoToStep<T>() where T : WizardStep => GoTo(_steps.FindIndex(s => s is T));

    public void StartOver()
    {
        _state.ResetResults();
        GoTo(0);
    }

    private async Task NextAsync()
    {
        var step = _steps[_index];
        if (step is ResultsStep) { StartOver(); return; }
        var problem = step.ValidateNext();
        if (problem != null)
        {
            MessageBox.Show(this, problem, step.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _next.Enabled = false; _back.Enabled = false;
        try
        {
            if (await step.OnNextAsync()) GoTo(_index + 1);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { RefreshButtons(); }
    }

    /// <summary>Lets a step advance programmatically (e.g. when analysis finishes).</summary>
    public void Advance() => GoTo(_index + 1);
}
