using System.Drawing.Drawing2D;

namespace LegionFanTray;

/// <summary>
/// Owner-drawn on/off switch: a caption, a sub-caption for live state, and a
/// pill on the right.
///
/// A stock CheckBox would have done the job, but every other bespoke control in
/// this app is GDI+ drawn (GaugeControl, RpmBar) and a themed CheckBox next to
/// them reads as a different application. The pill also makes the on/off state
/// legible at a glance from across the desk, which a 13 px tick does not.
/// </summary>
internal sealed class ToggleSwitch : Control
{
    private const int TrackW = 44;
    private const int TrackH = 22;

    private bool _checked;
    private bool _busy;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Caption = "";
        SubCaption = "";
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public string Caption { get; set; }

    private string _sub = "";

    /// <summary>Dim second line. Live state, not help text.</summary>
    public string SubCaption
    {
        get => _sub;
        set
        {
            if (_sub == value) return;
            _sub = value ?? "";
            Invalidate();
        }
    }

    public Color OnColor { get; set; } = Color.FromArgb(46, 139, 87);
    public Color OffColor { get; set; } = Color.FromArgb(214, 214, 218);
    public Color BorderColor { get; set; } = Color.FromArgb(198, 198, 204);
    public Color SubColor { get; set; } = Color.FromArgb(106, 106, 114);

    /// <summary>
    /// Set without raising CheckedChanged. Used when the state is coming *from*
    /// the machine (a refresh, or a failed write being rolled back) rather than
    /// from the user, where re-raising would start a write loop.
    /// </summary>
    public void SetCheckedSilently(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        Invalidate();
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Greys the pill while a write is in flight, without disabling the row.</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Invalidate();
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        if (Enabled && !_busy) Checked = !_checked;
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && Enabled && !_busy)
        {
            Checked = !_checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int trackX = Width - TrackW - 1;
        int trackY = (Height - TrackH) / 2;
        int textW = Math.Max(10, trackX - 10);

        bool dim = !Enabled || _busy;

        using var capFont = new Font(Font.FontFamily, 9f);
        using var subFont = new Font(Font.FontFamily, 8f);
        using var capBrush = new SolidBrush(dim ? SubColor : ForeColor);
        using var subBrush = new SolidBrush(SubColor);

        bool hasSub = !string.IsNullOrEmpty(_sub);
        float capH = g.MeasureString("Xg", capFont).Height;
        float subH = hasSub ? g.MeasureString("Xg", subFont).Height : 0f;
        float top = (Height - capH - subH) / 2f;

        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(Caption, capFont, capBrush, new RectangleF(0, top, textW, capH), fmt);
        if (hasSub)
            g.DrawString(_sub, subFont, subBrush, new RectangleF(0, top + capH, textW, subH), fmt);

        var track = new Rectangle(trackX, trackY, TrackW, TrackH);
        Color face = _checked ? OnColor : OffColor;
        if (dim) face = Blend(face, BackColor, 0.55f);

        using (var path = Pill(track))
        using (var brush = new SolidBrush(face))
        using (var pen = new Pen(dim ? Blend(BorderColor, BackColor, 0.5f) : BorderColor))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        int knob = TrackH - 6;
        int knobX = _checked ? track.Right - knob - 3 : track.Left + 3;
        using (var knobBrush = new SolidBrush(dim ? Blend(Color.White, BackColor, 0.3f) : Color.White))
            g.FillEllipse(knobBrush, knobX, trackY + 3, knob, knob);
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        int d = r.Height;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
}

