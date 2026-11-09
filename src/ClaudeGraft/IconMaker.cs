using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudeGraft;

/// Every icon is drawn rather than shipped: a rounded square in the Claude's
/// colour with its mark on it, so the tray and the shortcuts say which Claude
/// is which at a glance.
static partial class IconMaker
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public static Color Parse(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(0xC2, 0x41, 0x0C); }
    }

    /// "Claude 2" → "2", "Work" → "W". The part a person tells them apart by.
    public static string Mark(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "C";
        var last = words[^1];
        if (last.All(char.IsDigit)) return last.Length <= 2 ? last : last[..2];
        var first = words.FirstOrDefault(w => !w.Equals("Claude", StringComparison.OrdinalIgnoreCase)) ?? words[0];
        return char.ToUpperInvariant(first[0]).ToString();
    }

    public static Bitmap Draw(int size, Color color, string text, bool hollow = false, bool weekly = false)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var inset = Math.Max(1f, size / 16f);
        var box = new RectangleF(inset / 2, inset / 2, size - inset, size - inset);
        using var path = Rounded(box, size * 0.22f);
        if (hollow)
        {
            using var pen = new Pen(color, Math.Max(1.5f, size / 10f));
            g.DrawPath(pen, path);
        }
        else
        {
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
        }
        if (weekly)
        {
            // A week's figure, told apart from a five-hour one by the bar
            // under it: there is no room for a W beside the number.
            using var bar = new SolidBrush(hollow ? color : Color.White);
            g.FillRectangle(bar, size * 0.2f, size * 0.8f, size * 0.6f, Math.Max(1f, size / 14f));
        }

        if (text.Length > 0)
        {
            var scale = text.Length switch { 1 => 0.62f, 2 => 0.5f, _ => 0.4f };
            using var font = new Font("Segoe UI", size * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(hollow ? color : Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var area = new RectangleF(0, weekly ? -size * 0.06f : 0, size, size);
            g.DrawString(text, font, brush, area, format);
        }
        return bitmap;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// A tray icon owns a GDI handle; the caller hands back the old one.
    public static Icon TrayIcon(Color color, string text, bool weekly)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = Draw(size, color, text, weekly: weekly);
        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    public static Bitmap Dot(Color color, bool filled)
    {
        var size = 16;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (filled) { using var b = new SolidBrush(color); g.FillEllipse(b, 3, 3, 10, 10); }
        else { using var p = new Pen(color, 1.6f); g.DrawEllipse(p, 3.5f, 3.5f, 9, 9); }
        return bitmap;
    }

    /// A real multi-size .ico (PNG frames), which is what a shortcut wants:
    /// Explorer picks the frame for each view.
    public static void WriteIco(string path, Color color, string text)
    {
        int[] sizes = [16, 24, 32, 48, 256];
        var frames = sizes.Select(s =>
        {
            using var bmp = Draw(s, color, text);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path + ".tmp", FileMode.Create);
        using (var w = new BinaryWriter(file))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
            var offset = 6 + 16 * frames.Count;
            for (var i = 0; i < frames.Count; i++)
            {
                var s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
