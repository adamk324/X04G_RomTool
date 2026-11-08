using System.Drawing;
using System.Windows.Forms;
using X04G.Core.Common;

namespace X04G.Toolkit.App;

internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0, 95, 184);
    public static readonly Color PassBack = Color.FromArgb(223, 246, 221);
    public static readonly Color PassFore = Color.FromArgb(16, 124, 16);
    public static readonly Color WarnBack = Color.FromArgb(255, 244, 206);
    public static readonly Color WarnFore = Color.FromArgb(138, 86, 0);
    public static readonly Color FailBack = Color.FromArgb(253, 231, 233);
    public static readonly Color FailFore = Color.FromArgb(196, 43, 28);
    public static readonly Color InfoFore = Color.FromArgb(70, 70, 70);
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);
    public static readonly Color Panel = Color.FromArgb(243, 243, 243);
    public static readonly Color Border = Color.FromArgb(210, 210, 210);

    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Subtitle = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9f);
    public static readonly Font Mono = new("Consolas", 9f);
    public static readonly Font Badge = new("Segoe UI Semibold", 11f);

    public static (Color Back, Color Fore) For(Verdict v) => v switch
    {
        Verdict.Pass => (PassBack, PassFore),
        Verdict.Warning => (WarnBack, WarnFore),
        _ => (FailBack, FailFore),
    };

    public static Color For(Severity s) => s switch
    {
        Severity.Pass => PassFore,
        Severity.Warning => WarnFore,
        Severity.Fail => FailFore,
        _ => InfoFore,
    };
}

/// <summary>Single-column auto-sizing vertical stack. Labels anchored Left|Right wrap to the column width.</summary>
internal sealed class Stack : TableLayoutPanel
{
    public Stack()
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(0, 0, 12, 0);
        GrowStyle = TableLayoutPanelGrowStyle.AddRows;
    }

    public T Add<T>(T c, int topMargin = 0) where T : Control
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        if (topMargin > 0) c.Margin = new Padding(c.Margin.Left, topMargin, c.Margin.Right, c.Margin.Bottom);
        Controls.Add(c, 0, RowStyles.Count - 1);
        return c;
    }

    public void Clear()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        RowStyles.Clear();
        RowCount = 0;
        ResumeLayout();
    }
}

internal static class Ui
{
    public static Label Heading(string text) => new() { Text = text, Font = Theme.Title, AutoSize = true, ForeColor = Color.FromArgb(32, 32, 32), Margin = new Padding(0, 0, 0, 6) };
    public static Label Sub(string text) => new() { Text = text, Font = Theme.Subtitle, AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
    public static Label Para(string text, Color? color = null) => new() { Text = text, Font = Theme.Body, AutoSize = true, ForeColor = color ?? Color.FromArgb(40, 40, 40), Margin = new Padding(0, 2, 0, 4) };
    public static Label Muted(string text) => Para(text, Theme.Muted);

    public static Label Badge(Verdict v, string prefix)
    {
        var (b, f) = Theme.For(v);
        return new Label
        {
            Text = $"{prefix}: {v.ToString().ToUpperInvariant()}",
            BackColor = b, ForeColor = f, Font = Theme.Badge, AutoSize = true,
            Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 0, 8, 4),
        };
    }

    public static Label Status(string text, Verdict v)
    {
        var (b, f) = Theme.For(v);
        return new Label { Text = text, BackColor = b, ForeColor = f, AutoSize = true, Padding = new Padding(8, 5, 8, 5), Margin = new Padding(0, 4, 0, 4) };
    }

    public static TextBox LogBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = Theme.Mono, BackColor = Color.White, Dock = DockStyle.Fill,
    };

    /// <summary>Read-only rich text showing findings, colored by severity.</summary>
    public static RichTextBox FindingsBox(IEnumerable<(string Title, IEnumerable<Finding> Items)> groups, int height = 260)
    {
        var rt = new RichTextBox { ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Height = height, Font = Theme.Body, DetectUrls = false };
        foreach (var (title, items) in groups)
        {
            var list = items.ToList();
            if (list.Count == 0) continue;
            Append(rt, title + "\n", Color.Black, Theme.Bold);
            foreach (var f in list)
                Append(rt, $"  {Symbol(f.Severity)} {f.Severity.ToString().ToUpperInvariant(),-7}  {f.Message}\n", Theme.For(f.Severity), Theme.Body);
            Append(rt, "\n", Color.Black, Theme.Body);
        }
        rt.SelectionStart = 0;
        return rt;
    }

    private static string Symbol(Severity s) => s switch { Severity.Pass => "✔", Severity.Warning => "⚠", Severity.Fail => "✖", _ => "•" };

    public static void Append(RichTextBox rt, string text, Color color, Font font)
    {
        rt.SelectionStart = rt.TextLength;
        rt.SelectionLength = 0;
        rt.SelectionColor = color;
        rt.SelectionFont = font;
        rt.AppendText(text);
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static string Size(ulong? v) => v is ulong x ? ByteSize.Format(x) : "unknown";
}

/// <summary>Label + path text box + Browse button + help text.</summary>
internal sealed class FilePicker : TableLayoutPanel
{
    private readonly TextBox _box = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = Theme.Body };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _clear = new() { Text = "Clear", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly bool _folder;
    public event Action? PathChanged;

    public string? Path
    {
        get => string.IsNullOrWhiteSpace(_box.Text) ? null : _box.Text.Trim().Trim('"');
        set => _box.Text = value ?? "";
    }

    public string Filter { get; set; } = "Image files (*.img;*.bin)|*.img;*.bin|All files (*.*)|*.*";

    public FilePicker(string label, string help, bool required, bool folder = false)
    {
        _folder = folder;
        AutoSize = true;
        ColumnCount = 3;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Margin = new Padding(0, 6, 0, 6);
        var title = new Label { Text = label + (required ? "  (required)" : "  (optional)"), Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        var helpLabel = new Label { Text = help, AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 3) };
        Controls.Add(title, 0, 0); SetColumnSpan(title, 3);
        Controls.Add(helpLabel, 0, 1); SetColumnSpan(helpLabel, 3);
        Controls.Add(_box, 0, 2);
        Controls.Add(_browse, 1, 2);
        Controls.Add(_clear, 2, 2);
        _clear.Visible = !required;
        _browse.Click += (_, _) => Browse();
        _clear.Click += (_, _) => { _box.Text = ""; };
        _box.TextChanged += (_, _) => PathChanged?.Invoke();
    }

    private void Browse()
    {
        if (_folder)
        {
            using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Select the output folder", ShowNewFolderButton = true };
            if (Path != null && Directory.Exists(Path)) d.SelectedPath = Path;
            if (d.ShowDialog(FindForm()) == DialogResult.OK) _box.Text = d.SelectedPath;
        }
        else
        {
            using var d = new OpenFileDialog { Filter = Filter, CheckFileExists = true, Multiselect = false, Title = "Select file (it will only be read, never modified)" };
            if (Path != null && File.Exists(Path)) d.InitialDirectory = System.IO.Path.GetDirectoryName(Path);
            if (d.ShowDialog(FindForm()) == DialogResult.OK) _box.Text = d.FileName;
        }
    }
}

/// <summary>Horizontal bar showing metadata area, partitions and free space of a super layout.</summary>
internal sealed class StorageBar : Control
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(0, 120, 212), Color.FromArgb(0, 153, 112), Color.FromArgb(136, 23, 152),
        Color.FromArgb(202, 80, 16), Color.FromArgb(78, 100, 160), Color.FromArgb(160, 120, 0),
    };

    public string Caption { get; set; } = "";
    public ulong Total { get; set; }
    public List<(string Name, ulong Start, ulong Length)> Segments { get; } = new();
    public ulong MetadataBytes { get; set; }
    public bool Overflow { get; set; }

    public StorageBar()
    {
        DoubleBuffered = true;
        Height = 62;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Margin = new Padding(0, 4, 0, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var captionBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
        g.DrawString(Caption, Theme.Bold, captionBrush, 0, 0);
        var bar = new Rectangle(0, 20, Width - 1, 22);
        using (var free = new SolidBrush(Color.FromArgb(235, 235, 235))) g.FillRectangle(free, bar);
        if (Total > 0)
        {
            float Scale(ulong v) => (float)((double)v / Total * bar.Width);
            using (var meta = new SolidBrush(Color.FromArgb(150, 150, 150)))
                g.FillRectangle(meta, bar.X, bar.Y, Math.Max(2f, Scale(MetadataBytes)), bar.Height);
            int i = 0;
            var legend = new List<(string, Color)> { ("metadata", Color.FromArgb(150, 150, 150)) };
            foreach (var (name, start, len) in Segments)
            {
                var c = Palette[i++ % Palette.Length];
                using var b = new SolidBrush(c);
                float x = bar.X + Scale(Math.Min(start, Total));
                float w = Math.Max(2f, Scale(Math.Min(len, Total - Math.Min(start, Total))));
                g.FillRectangle(b, x, bar.Y, w, bar.Height);
                legend.Add((name, c));
            }
            if (Overflow)
            {
                using var p = new Pen(Theme.FailFore, 3);
                g.DrawRectangle(p, bar);
            }
            float lx = 0;
            foreach (var (n, c) in legend)
            {
                using var b = new SolidBrush(c);
                g.FillRectangle(b, lx, 47, 10, 10);
                g.DrawString(n, Theme.Body, captionBrush, lx + 13, 44);
                lx += 13 + g.MeasureString(n, Theme.Body).Width + 12;
            }
        }
        using var border = new Pen(Theme.Border);
        g.DrawRectangle(border, bar);
    }
}
