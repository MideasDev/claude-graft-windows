namespace ClaudeGraft.Ui;

/// One row of a long list: a title, a line under it, words on the right,
/// and the colour of the account it belongs to.
sealed record ListRow(object Tag, string Title, string Subtitle, string Right, Color? Swatch = null, string Mark = "",
                      bool Dim = false, string Badge = "");

/// A list that draws only the rows on screen, so hundreds of artifacts cost
/// no more than the dozen visible. Each row has a ⋯ at its right for its menu,
/// opens on a double click or Return, and takes a right click for the menu.
sealed class ItemList : ScrollableControl
{
    List<ListRow> rows = [];
    int hover = -1, selected = -1;
    readonly int dpi = Theme.Dpi;
    public event Action<ListRow>? Open;
    public event Action<ListRow, Point>? Menu;
    public string Empty { get; set; } = "";

    int S(float px) => Theme.S(px, dpi);
    int RowHeight => S(58);
    Theme.FontSet F => Theme.For(dpi);

    public ItemList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Theme.Window;
        TabStop = true;
        Theme.Scrolling(this);
    }

    public void SetRows(List<ListRow> next)
    {
        rows = next;
        hover = selected = -1;
        AutoScrollMinSize = new Size(0, rows.Count * RowHeight + S(8));
        Invalidate();
    }

    int RowAt(Point p)
    {
        var y = p.Y - AutoScrollPosition.Y;
        var i = y / RowHeight;
        return i >= 0 && i < rows.Count ? i : -1;
    }

    Rectangle MoreBox(int i) =>
        new(ClientSize.Width - S(40), i * RowHeight + AutoScrollPosition.Y + (RowHeight - S(30)) / 2, S(30), S(30));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = RowAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var i = RowAt(e.Location);
        if (i < 0) return;
        selected = i;
        Invalidate();
        if (e.Button == MouseButtons.Right || MoreBox(i).Contains(e.Location))
            Menu?.Invoke(rows[i], e.Button == MouseButtons.Right ? e.Location : new Point(MoreBox(i).Left, MoreBox(i).Bottom));
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        var i = RowAt(e.Location);
        if (i >= 0 && !MoreBox(i).Contains(e.Location)) Open?.Invoke(rows[i]);
        base.OnMouseDoubleClick(e);
    }

    protected override bool IsInputKey(Keys key) => key is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(key);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (rows.Count == 0) return;
        if (e.KeyCode == Keys.Down) selected = Math.Min(rows.Count - 1, selected + 1);
        else if (e.KeyCode == Keys.Up) selected = Math.Max(0, selected - 1);
        else if (e.KeyCode == Keys.Enter && selected >= 0) Open?.Invoke(rows[selected]);
        else if (e.KeyCode == Keys.Apps && selected >= 0) Menu?.Invoke(rows[selected], new Point(S(40), (selected + 1) * RowHeight + AutoScrollPosition.Y));
        else return;
        // Keep the selected row in view.
        var top = selected * RowHeight;
        if (top + AutoScrollPosition.Y < 0) AutoScrollPosition = new Point(0, top);
        else if (top + RowHeight + AutoScrollPosition.Y > ClientSize.Height) AutoScrollPosition = new Point(0, top + RowHeight - ClientSize.Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        g.Clear(Theme.Window);
        if (rows.Count == 0)
        {
            TextRenderer.DrawText(g, Empty, F.Body, new Rectangle(0, S(20), ClientSize.Width, S(40)), Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var first = Math.Max(0, -AutoScrollPosition.Y / RowHeight);
        var last = Math.Min(rows.Count - 1, (-AutoScrollPosition.Y + ClientSize.Height) / RowHeight);
        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var top = i * RowHeight + AutoScrollPosition.Y;
            var box = new Rectangle(S(2), top + S(2), ClientSize.Width - S(4), RowHeight - S(4));
            if (i == selected || i == hover)
            {
                using var path = Theme.Round(box, S(8));
                using var brush = new SolidBrush(i == selected ? Theme.Card : Theme.Hover);
                g.FillPath(brush, path);
            }
            var x = S(12);
            if (row.Swatch is { } colour)
            {
                using var bitmap = IconMaker.Draw(S(22), colour, row.Mark);
                g.DrawImage(bitmap, x, top + (RowHeight - S(22)) / 2);
                x += S(34);
            }
            var rightWidth = string.IsNullOrEmpty(row.Right) ? 0 : Math.Min(S(220), TextRenderer.MeasureText(row.Right, F.Small).Width + S(8));
            var textRight = ClientSize.Width - S(48) - rightWidth;
            var ink = row.Dim ? Theme.SubText : Theme.Text;
            var title = row.Title;
            TextRenderer.DrawText(g, title, F.BodyBold, new Rectangle(x, top + S(9), textRight - x, S(20)), ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (!string.IsNullOrEmpty(row.Badge))
            {
                var titleWidth = Math.Min(textRight - x, TextRenderer.MeasureText(title, F.BodyBold).Width);
                TextRenderer.DrawText(g, row.Badge, F.Small, new Rectangle(x + titleWidth + S(6), top + S(10), S(120), S(18)), Theme.Amber,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            TextRenderer.DrawText(g, row.Subtitle, F.Small, new Rectangle(x, top + S(31), textRight - x, S(18)), Theme.SubText,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (rightWidth > 0)
                TextRenderer.DrawText(g, row.Right, F.Small, new Rectangle(textRight, top, rightWidth, RowHeight), Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            var more = MoreBox(i);
            if (i == hover || i == selected)
                TextRenderer.DrawText(g, Theme.GMore, F.Icon, more, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
