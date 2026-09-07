using System.Drawing.Drawing2D;

namespace LegionFanTray;

/// <summary>GDI+ arc gauge. No third-party UI library needed.</summary>
internal sealed class GaugeControl : Control
{
    private const float StartAngle = 135f;
    private const float SweepAngle = 270f;

    private int? _value;

    public GaugeControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Caption = "";
        Unit = "°C";
        Minimum = 0;
        Maximum = 110;
        WarnAt = 65;
        HotAt = 85;
    }

    public string Caption { get; set; }
    public string Unit { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; }
    public int WarnAt { get; set; }
    public int HotAt { get; set; }

    public int? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }
    }

    private Color ValueColor => _value is null
        ? Color.Gray
        : _value >= HotAt ? Color.FromArgb(208, 52, 43)
        : _value >= WarnAt ? Color.FromArgb(224, 155, 32)
        : Color.FromArgb(52, 168, 83);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        float pad = 10f;
        float size = Math.Min(Width, Height - 16) - pad * 2;
        if (size <= 10) return;
        var rect = new RectangleF((Width - size) / 2f, pad, size, size);
        float thickness = Math.Max(6f, size * 0.11f);

        // Track.
        using (var track = new Pen(Color.FromArgb(222, 222, 228), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(track, rect.X + thickness / 2, rect.Y + thickness / 2,
                rect.Width - thickness, rect.Height - thickness, StartAngle, SweepAngle);
        }

        // Filled portion.
        if (_value.HasValue)
        {
            float span = Math.Max(1, Maximum - Minimum);
            float frac = Math.Clamp((_value.Value - Minimum) / span, 0f, 1f);
            if (frac > 0.001f)
            {
                using var pen = new Pen(ValueColor, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, rect.X + thickness / 2, rect.Y + thickness / 2,
                    rect.Width - thickness, rect.Height - thickness, StartAngle, SweepAngle * frac);
            }
        }

        // Reading.
        string text = _value.HasValue ? _value.Value.ToString() : "n/a";
        using var valueFont = new Font(Font.FontFamily, Math.Max(9f, size * 0.24f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font(Font.FontFamily, Math.Max(7f, size * 0.11f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var capFont = new Font(Font.FontFamily, Math.Max(8f, size * 0.115f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(ForeColor);
        using var dim = new SolidBrush(Color.FromArgb(150, ForeColor));

        var vSize = g.MeasureString(text, valueFont);
        var uSize = _value.HasValue ? g.MeasureString(Unit, unitFont) : SizeF.Empty;
        float totalW = vSize.Width + uSize.Width;
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;

        g.DrawString(text, valueFont, fg, cx - totalW / 2f, cy - vSize.Height / 2f);
        if (_value.HasValue)
        {
            g.DrawString(Unit, unitFont, dim, cx - totalW / 2f + vSize.Width, cy - vSize.Height / 2f + vSize.Height * 0.28f);
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            var cSize = g.MeasureString(Caption, capFont);
            g.DrawString(Caption, capFont, dim, cx - cSize.Width / 2f, rect.Bottom - cSize.Height * 1.6f);
        }
    }
}

/// <summary>Horizontal RPM bar drawn against the firmware ceiling.</summary>
internal sealed class RpmBar : Control
{
    private int? _value;

    public RpmBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Caption = "Fan";
        Maximum = LenovoWmi.FallbackMaxRpm;
        Height = 30;
    }

    public string Caption { get; set; }
    public int Maximum { get; set; }

    public int? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using var font = new Font(Font.FontFamily, 8.25f);
        using var fg = new SolidBrush(ForeColor);
        using var dim = new SolidBrush(Color.FromArgb(160, ForeColor));

        string label = Caption;
        string reading = (_value?.ToString() ?? "n/a") + " rpm";
        var labelSize = g.MeasureString(label, font);
        var readingSize = g.MeasureString(reading, font);

        g.DrawString(label, font, dim, 0, 0);
        g.DrawString(reading, font, fg, Width - readingSize.Width, 0);

        float barTop = labelSize.Height + 3;
        float barH = Math.Max(6f, Height - barTop - 2);
        var barRect = new RectangleF(0, barTop, Width, barH);

        using (var back = new SolidBrush(Color.FromArgb(222, 222, 228)))
            g.FillRectangle(back, barRect);

        if (_value.HasValue && Maximum > 0)
        {
            float frac = Math.Clamp(_value.Value / (float)Maximum, 0f, 1f);
            // At the ceiling the bar goes red, matching "pinned".
            var c = frac >= 0.95f ? Color.FromArgb(208, 52, 43)
                  : frac >= 0.70f ? Color.FromArgb(224, 155, 32)
                  : Color.FromArgb(52, 168, 83);
            using var fill = new SolidBrush(c);
            g.FillRectangle(fill, new RectangleF(barRect.X, barRect.Y, barRect.Width * frac, barRect.Height));
        }
    }
}
