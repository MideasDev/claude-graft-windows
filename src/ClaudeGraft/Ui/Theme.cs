using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeGraft.Ui;

/// Colours and type for every window, following the Windows app theme
/// (light or dark) the way the macOS original follows the system.
static partial class Theme
{
    public static bool Dark { get; private set; }

    public static Color Window, Sidebar, Card, Line, Text, SubText, Faint, Accent, AccentText, Selected, SelectedText,
                        Hover, Track, Green, Amber, Red, Button, ButtonHover, Input, InputLine;

    /// The DPI windows are being built for. Every font is made in pixels for
    /// a DPI and every size scaled by one, so text drawn by a label, a button
    /// or a painted row comes out the same size: point fonts were measured at
    /// one scale and drawn at another, which at 125% left button text small
    /// and off-centre. It is not fixed: a window on a 100% screen beside a
    /// 125% laptop builds for 100%, and each control keeps the DPI it was
    /// built for, so the dropdown on one screen never resizes the window on
    /// the other.
    public static int Dpi { get; private set; } = DpiAt(Cursor.Position);

    public static void Use(int dpi) => Dpi = dpi > 0 ? dpi : 96;

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(long point, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    /// The effective DPI of the screen holding this point.
    public static int DpiAt(Point point)
    {
        try
        {
            // POINT passed by value: x in the low half, y in the high half.
            var monitor = MonitorFromPoint(((long)point.Y << 32) | (uint)point.X, 2);
            if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) return (int)x;
        }
        catch { }
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return (int)Math.Round(g.DpiY); }
        catch { return 96; }
    }

    public static int S(float px) => S(px, Dpi);
    public static int S(float px, int dpi) => (int)Math.Round(px * dpi / 96f);

    public static readonly string IconFamily = Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static readonly string MonoFamily = Installed("Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    public sealed class FontSet(int dpi)
    {
        Font Px(string family, float points, FontStyle style = FontStyle.Regular) =>
            new(family, points * dpi / 72f, style, GraphicsUnit.Pixel);

        public readonly Font Body = null!, BodyBold = null!, Small = null!, Heading = null!, Title = null!, Mono = null!,
                             Icon = null!, IconSmall = null!, IconLarge = null!;

        public FontSet Make() => new(dpi, true);

        FontSet(int dpi, bool _) : this(dpi)
        {
            Body = Px("Segoe UI", 9.75f);
            BodyBold = Px("Segoe UI Semibold", 9.75f);
            Small = Px("Segoe UI", 8.5f);
            Heading = Px("Segoe UI Semibold", 11f);
            Title = Px("Segoe UI Semibold", 10.5f);
            Mono = Px(MonoFamily, 9f);
            Icon = Px(IconFamily, 11f);
            IconSmall = Px(IconFamily, 9f);
            IconLarge = Px(IconFamily, 15f);
        }
    }

    static readonly Dictionary<int, FontSet> Sets = [];

    public static FontSet For(int dpi)
    {
        lock (Sets)
        {
            if (!Sets.TryGetValue(dpi, out var set)) Sets[dpi] = set = new FontSet(dpi).Make();
            return set;
        }
    }

    public static Font Body => For(Dpi).Body;
    public static Font BodyBold => For(Dpi).BodyBold;
    public static Font Small => For(Dpi).Small;
    public static Font Heading => For(Dpi).Heading;
    public static Font Title => For(Dpi).Title;
    public static Font Mono => For(Dpi).Mono;
    public static Font Icon => For(Dpi).Icon;
    public static Font IconSmall => For(Dpi).IconSmall;
    public static Font IconLarge => For(Dpi).IconLarge;

    // Glyphs shared by Segoe MDL2 Assets and Segoe Fluent Icons.
    public const string GHome = "", GLink = "", GAdd = "", GFolder = "", GRefresh = "",
                        GInfo = "", GRemove = "", GSettings = "", GMore = "", GSync = "",
                        GDown = "", GEdit = "", GPeople = "", GOpen = "", GLog = "",
                        GHeart = "", GClose = "", GLibrary = "", GProjects = "", GSearch = "";

    static bool Installed(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase));
    }

    static Theme() => Load();

    public static void Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            Dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { Dark = false; }

        Accent = Color.FromArgb(0x25, 0x63, 0xEB);
        AccentText = Color.White;
        Green = Color.FromArgb(0x4A, 0xC2, 0x6B);
        Amber = Color.FromArgb(0xE0, 0x9B, 0x2D);
        Red = Color.FromArgb(0xE5, 0x48, 0x4D);
        if (Dark)
        {
            Window = Color.FromArgb(0x2A, 0x28, 0x26);
            Sidebar = Color.FromArgb(0x22, 0x21, 0x1F);
            Card = Color.FromArgb(0x35, 0x33, 0x31);
            Line = Color.FromArgb(0x48, 0x45, 0x42);
            Text = Color.FromArgb(0xF2, 0xF0, 0xED);
            SubText = Color.FromArgb(0xA8, 0xA4, 0x9F);
            Faint = Color.FromArgb(0x7C, 0x78, 0x73);
            Selected = Accent;
            SelectedText = Color.White;
            Hover = Color.FromArgb(0x33, 0x31, 0x2F);
            Track = Color.FromArgb(0x50, 0x4D, 0x4A);
            Button = Color.FromArgb(0x45, 0x42, 0x3F);
            ButtonHover = Color.FromArgb(0x52, 0x4F, 0x4B);
            Input = Color.FromArgb(0x26, 0x24, 0x22);
            InputLine = Color.FromArgb(0x4A, 0x47, 0x44);
        }
        else
        {
            Window = Color.FromArgb(0xF7, 0xF6, 0xF4);
            Sidebar = Color.FromArgb(0xEC, 0xEA, 0xE6);
            Card = Color.White;
            Line = Color.FromArgb(0xE2, 0xDF, 0xDA);
            Text = Color.FromArgb(0x1F, 0x1D, 0x1B);
            SubText = Color.FromArgb(0x6B, 0x67, 0x62);
            Faint = Color.FromArgb(0x9A, 0x96, 0x91);
            Selected = Accent;
            SelectedText = Color.White;
            Hover = Color.FromArgb(0xE0, 0xDD, 0xD8);
            Track = Color.FromArgb(0xE4, 0xE1, 0xDC);
            Button = Color.FromArgb(0xE6, 0xE3, 0xDF);
            ButtonHover = Color.FromArgb(0xDA, 0xD6, 0xD1);
            Input = Color.FromArgb(0xF4, 0xF2, 0xEF);
            InputLine = Color.FromArgb(0xD8, 0xD4, 0xCF);
        }
    }

    /// The bar's colour moves from green to amber to red as the window fills.
    public static Color Fill(int percent) => percent >= 90 ? Red : percent >= 70 ? Amber : Green;

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    // ---- Window chrome ----

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// A dark title bar on a dark window, and rounded corners where Windows 11
    /// draws them; both are ignored where unsupported.
    public static void Chrome(Form form, bool rounded = false)
    {
        try
        {
            var dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            if (rounded)
            {
                var round = 2;
                DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
            }
        }
        catch { }
    }

    public static float Scale(Control c) => Dpi / 96f;

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// Scrollbars in the window's colours: Windows' own dark ones in dark
    /// mode, and only ever up and down.
    public static void Scrolling(ScrollableControl panel)
    {
        panel.AutoScroll = false;
        panel.HorizontalScroll.Enabled = false;
        panel.HorizontalScroll.Visible = false;
        panel.HorizontalScroll.Maximum = 0;
        panel.AutoScroll = true;
        void Dress() { if (Dark) try { SetWindowTheme(panel.Handle, "DarkMode_Explorer", null); } catch { } }
        if (panel.IsHandleCreated) Dress(); else panel.HandleCreated += (_, _) => Dress();
    }

    /// Paints a window and all its children in one pass, off screen: what
    /// stops a window built of painted controls flashing as it changes.
    public const int Composited = 0x02000000;
}

/// Menus in the window's colours.
sealed class ThemedRenderer() : ToolStripProfessionalRenderer(new Colors())
{
    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
        public override Color CheckBackground => Theme.Hover;
        public override Color CheckSelectedBackground => Theme.Hover;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.SubText;
        base.OnRenderArrow(e);
    }

    public static ContextMenuStrip Menu() => new() { Renderer = new ThemedRenderer(), Font = Theme.Body, ShowImageMargin = false };
}
