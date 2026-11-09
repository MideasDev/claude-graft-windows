using System.ComponentModel;

namespace ClaudeGraft.Ui;

/// A control that paints itself, double-buffered, transparent to the parent.
abstract class Surface : Control
{
    protected Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected bool Hovered;
    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }
    /// The DPI this control was built for, and the fonts for it.
    protected readonly int BuiltDpi = Theme.Dpi;
    protected Theme.FontSet Fonts => Theme.For(BuiltDpi);
    protected int S(float px) => Theme.S(px, BuiltDpi);
}

/// A rounded panel: the cards the window is built from.
class Card : Panel
{
    public Color Fill { get; set; } = Theme.Card;
    public int Radius { get; set; } = 10;
    readonly int builtDpi = Theme.Dpi;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Padding = new Padding(14, 10, 14, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius * builtDpi / 96f);
        using var brush = new SolidBrush(Fill);
        e.Graphics.FillPath(brush, path);
    }
}

/// Text drawn by this app rather than by a Label, so it is measured and
/// drawn in the same pixel font as everything else. Wraps when given a
/// maximum width.
class TextLabel : Surface
{
    Font face;
    int wrapWidth;
    bool auto = true;

    public TextLabel(string text, Font? font = null, Color? color = null)
    {
        face = font ?? Theme.Body;
        ForeColor = color ?? Theme.Text;
        Margin = new Padding(0);
        base.Text = text;
        Measure();
    }

    public Font Face { get => face; set { face = value; Measure(); Invalidate(); } }

    public new Size MaximumSize { get => new(wrapWidth, 0); set { wrapWidth = value.Width; Measure(); } }

    public override bool AutoSize { get => auto; set { auto = value; Measure(); } }

    TextFormatFlags Flags => TextFormatFlags.NoPadding |
        (wrapWidth > 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

    void Measure()
    {
        if (!auto) return;
        var room = new Size(wrapWidth > 0 ? wrapWidth : int.MaxValue, int.MaxValue);
        var size = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, face, room, Flags);
        Size = new Size(size.Width + Padding.Horizontal + 1, size.Height + Padding.Vertical);
    }

    protected override void OnTextChanged(EventArgs e) { Measure(); Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaddingChanged(EventArgs e) { Measure(); base.OnPaddingChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var box = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        TextRenderer.DrawText(e.Graphics, Text, face, box, ForeColor, Flags);
    }
}

/// A button drawn the way the original's are: rounded, a filled primary,
/// a quiet secondary, a glyph alone, or a glyph with words.
sealed class FlatButton : Surface
{
    public enum Kind { Secondary, Primary, Quiet, Danger }

    public Kind Style { get; set; } = Kind.Secondary;
    string? glyph;
    bool chevron;
    /// Either set after the button is made resizes it, as its text does: a
    /// chevron given in an initializer left the label cut short ("Tod…").
    public string? Glyph { get => glyph; set { glyph = value; AutoSizeNow(); Invalidate(); } }
    public bool Chevron { get => chevron; set { chevron = value; AutoSizeNow(); Invalidate(); } }
    /// Rows of a menu-like list read from the left; buttons are centred.
    public bool AlignLeft { get; set; }
    readonly Font face = Theme.Body;

    public FlatButton(string text, Kind style = Kind.Secondary, string? glyph = null)
    {
        base.Text = text;
        Style = style;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Margin = new Padding(6, 0, 0, 0);
        AutoSizeNow();
    }

    int TextWidth => string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, face, Size.Empty, TextFormatFlags.NoPadding).Width;

    public void AutoSizeNow()
    {
        var width = TextWidth;
        if (Glyph != null) width += S(string.IsNullOrEmpty(Text) ? 16 : 24);
        if (Chevron) width += S(18);
        Size = new Size(Math.Max(S(30), width + S(string.IsNullOrEmpty(Text) ? 14 : 28)), S(30));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AutoSizeNow(); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var (fill, ink) = Style switch
        {
            Kind.Primary => (Hovered ? ControlPaint.Light(Theme.Accent, 0.1f) : Theme.Accent, Theme.AccentText),
            Kind.Danger => (Hovered ? Theme.ButtonHover : Theme.Button, Theme.Red),
            Kind.Quiet => (Hovered ? Theme.Hover : Color.Transparent, Theme.Text),
            _ => (Hovered ? Theme.ButtonHover : Theme.Button, Theme.Text),
        };
        if (!Enabled) { fill = Style == Kind.Quiet ? Color.Transparent : Theme.Button; ink = Theme.Faint; }
        if (fill.A > 0)
        {
            using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), S(7));
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (string.IsNullOrEmpty(Text))
        {
            if (Glyph != null) TextRenderer.DrawText(g, Glyph, Fonts.Icon, ClientRectangle, ink, centre);
            return;
        }
        // Glyph, words and chevron laid out as one group, centred unless
        // the button is a row of a list.
        var glyphWidth = Glyph != null ? S(24) : 0;
        var chevronWidth = Chevron ? S(18) : 0;
        var textWidth = Math.Min(TextWidth, Width - glyphWidth - chevronWidth - S(16));
        var group = glyphWidth + textWidth + chevronWidth;
        var x = AlignLeft ? S(10) : Math.Max(S(8), (Width - group) / 2);
        if (Glyph != null)
        {
            TextRenderer.DrawText(g, Glyph, Fonts.Icon, new Rectangle(x, 0, S(18), Height), ink, centre);
            x += glyphWidth;
        }
        TextRenderer.DrawText(g, Text, face, new Rectangle(x, 0, textWidth + S(2), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Chevron)
        {
            var chevronX = AlignLeft ? Width - S(26) : x + textWidth + S(6);
            TextRenderer.DrawText(g, Theme.GDown, Fonts.IconSmall, new Rectangle(chevronX, 0, S(14), Height), Theme.SubText, centre);
        }
    }
}

/// One row of the sidebar: a mark, a title and a line under it.
sealed class SidebarItem : Surface
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? Glyph { get; set; }
    public Color? Swatch { get; set; }
    public string SwatchMark { get; set; } = "";
    public bool Selected { get; set; }
    public bool Running { get; set; }
    public object? Tag2 { get; set; }

    public SidebarItem()
    {
        Cursor = Cursors.Hand;
        Height = 50;
        Margin = new Padding(0, 0, 0, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        if (Selected || Hovered)
        {
            using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), S(8));
            using var brush = new SolidBrush(Selected ? Theme.Selected : Theme.Hover);
            g.FillPath(brush, path);
        }
        var ink = Selected ? Theme.SelectedText : Theme.Text;
        var sub = Selected ? Color.FromArgb(220, Theme.SelectedText) : Theme.SubText;
        var mark = new Rectangle(S(10), (Height - S(22)) / 2, S(22), S(22));
        if (Swatch is { } colour)
        {
            using var bitmap = IconMaker.Draw(mark.Width, colour, SwatchMark);
            g.DrawImage(bitmap, mark);
            if (Running)
            {
                var dot = new Rectangle(mark.Right - S(6), mark.Bottom - S(6), S(9), S(9));
                using var ring = new SolidBrush(Selected ? Theme.Selected : Theme.Sidebar);
                g.FillEllipse(ring, Rectangle.Inflate(dot, S(2), S(2)));
                using var green = new SolidBrush(Theme.Green);
                g.FillEllipse(green, dot);
            }
        }
        else if (Glyph != null)
            TextRenderer.DrawText(g, Glyph, Fonts.IconLarge, mark, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var left = S(42);
        if (string.IsNullOrEmpty(Subtitle))
            TextRenderer.DrawText(g, Title, Fonts.Body, new Rectangle(left, 0, Width - left - S(6), Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            TextRenderer.DrawText(g, Title, Fonts.BodyBold, new Rectangle(left, S(7), Width - left - S(6), S(20)), ink, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Subtitle, Fonts.Small, new Rectangle(left, S(26), Width - left - S(6), S(18)), sub, TextFormatFlags.EndEllipsis);
        }
    }
}

/// One usage window: name, bar, percentage and the time to its reset.
sealed class UsageRow : Surface
{
    public string Label { get; set; } = "";
    public int Percent { get; set; }
    public string Reset { get; set; } = "";
    public bool Compact { get; set; }

    public UsageRow() { Height = 26; Margin = new Padding(0); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var labelWidth = S(Compact ? 64 : 80);
        var resetWidth = string.IsNullOrEmpty(Reset) || Compact ? 0 : S(96);
        var percentWidth = S(48);
        TextRenderer.DrawText(g, Label, Fonts.Body, new Rectangle(0, 0, labelWidth, Height), Theme.SubText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var barLeft = labelWidth;
        var barRight = Width - percentWidth - resetWidth - S(8);
        var bar = new RectangleF(barLeft, Height / 2f - S(3), Math.Max(0, barRight - barLeft), S(6));
        using (var track = new SolidBrush(Theme.Track))
        using (var path = Theme.Round(bar, S(3)))
            g.FillPath(track, path);
        var filled = bar with { Width = Math.Max(S(4), bar.Width * Math.Clamp(Percent, 0, 100) / 100f) };
        using (var fill = new SolidBrush(Theme.Fill(Percent)))
        using (var path = Theme.Round(filled, S(3)))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, Percent + "%", Fonts.Body, new Rectangle(barRight, 0, percentWidth, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        if (resetWidth > 0)
            TextRenderer.DrawText(g, Reset, Fonts.Body, new Rectangle(Width - resetWidth, 0, resetWidth, Height), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

/// An on/off switch, as the original's Settings window uses.
sealed class Toggle : Surface
{
    bool on;
    public event EventHandler? Changed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => on;
        set { if (on == value) return; on = value; Invalidate(); }
    }

    public Toggle() { Size = new Size(44, 24); Cursor = Cursors.Hand; }

    protected override void OnClick(EventArgs e)
    {
        On = !On;
        Changed?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(track, track.Height / 2))
        using (var brush = new SolidBrush(On ? Theme.Accent : Theme.Track))
            g.FillPath(brush, path);
        var knob = Height - S(6);
        var x = On ? Width - knob - S(3) : S(3);
        using var white = new SolidBrush(Color.White);
        g.FillEllipse(white, x, S(3), knob, knob);
    }
}

/// A text box set inside a rounded field.
sealed class Field : Card
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None };

    public Field(string text, Font? font = null, bool readOnly = false)
    {
        Fill = Theme.Input;
        Radius = 7;
        Padding = new Padding(Theme.S(9), Theme.S(6), Theme.S(9), Theme.S(4));
        Box.Text = text;
        var face = font ?? Theme.Body;
        Box.Font = face;
        // Windows Forms rescales a text box's font by itself when the screen
        // scale changes, on top of the font already made for that scale:
        // measured, the folder name came out at four fifths of the text
        // around it after a move to a 100% screen. It keeps the one given.
        Box.HandleCreated += (_, _) => Box.Font = face;
        Box.DpiChangedAfterParent += (_, _) => Box.Font = face;
        Box.ForeColor = Theme.Text;
        Box.BackColor = Theme.Input;
        Box.ReadOnly = readOnly;
        Box.Dock = DockStyle.Fill;
        Controls.Add(Box);
        Height = Theme.S(32);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }
}
