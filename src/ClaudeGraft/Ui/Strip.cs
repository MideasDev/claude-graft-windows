namespace ClaudeGraft.Ui;

/// A row with things on the left and things on the right, each centred on
/// the row's height: the shape of nearly every line in the window.
sealed class Strip : Panel
{
    public FlowLayoutPanel Start { get; } = Flow(FlowDirection.LeftToRight);
    public FlowLayoutPanel End { get; } = Flow(FlowDirection.RightToLeft);

    static FlowLayoutPanel Flow(FlowDirection direction) => new()
    {
        FlowDirection = direction, WrapContents = false, BackColor = Color.Transparent,
        Margin = Padding.Empty, Padding = Padding.Empty,
    };

    public Strip(int height)
    {
        Height = height;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Start.Dock = DockStyle.Fill;
        End.Dock = DockStyle.Right;
        End.AutoSize = true;
        End.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(Start);
        Controls.Add(End);
    }

    public new Strip Left(Control c, int gap = 8) { Place(c, new Padding(0, 0, gap, 0)); Start.Controls.Add(c); return this; }
    public new Strip Right(Control c, int gap = 6) { Place(c, new Padding(gap, 0, 0, 0)); End.Controls.Add(c); return this; }

    void Place(Control c, Padding side)
    {
        var top = Math.Max(0, (Height - c.Height) / 2);
        c.Margin = new Padding(side.Left, top, side.Right, 0);
    }
}
