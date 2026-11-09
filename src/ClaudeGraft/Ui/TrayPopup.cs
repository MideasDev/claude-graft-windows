using ClaudeGraft.Core;

namespace ClaudeGraft.Ui;

/// What opens from the notification area, as the original's dropdown does:
/// every Claude with its usage and an Open button, the conversations shared
/// usage is carrying, two switches, and the few things done from here.
sealed class TrayPopup : Form
{
    readonly TrayApp app;
    readonly FlowLayoutPanel stack = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly ToolTip tips = new() { InitialDelay = 300 };
    DateTime hiddenAt = DateTime.MinValue;
    string shown = "";

    /// Everything the dropdown shows, to the minute: it is rebuilt while open
    /// only when this changes.
    string Signature()
    {
        var now = DateTime.UtcNow;
        var parts = new List<string>();
        foreach (var e in app.Entries())
        {
            var f = app.Monitor.Entry(e.ProfileDir);
            parts.Add($"{e.Name}|{Entries.Running(e, app.Snapshot)}|{f?.FiveHour}|{f?.Week}|{Countdown.Until(f?.FiveHourReset, now)}|" +
                      $"{Countdown.Until(f?.WeekReset, now)}|{(f == null ? 0 : (int)(now - f.Taken).TotalMinutes)}|{app.Monitor.Note(e.ProfileDir)}");
        }
        foreach (var (session, title, _) in app.Pool.Conversations()) parts.Add($"{session.Id}|{title}|{session.ServedBy}");
        parts.Add($"{SafeStartup()}|{app.Store.Data.LiveUsage}|{app.Updates.ClaudeLine()}");
        return string.Join("\n", parts);
    }
    Point anchor;

    public TrayPopup(TrayApp app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        stack.BackColor = Theme.Card;
        stack.Padding = new Padding(Px(14), Px(10), Px(14), Px(10));
        Controls.Add(stack);
        Deactivate += (_, _) => { hiddenAt = DateTime.UtcNow; Hide(); };
        HandleCreated += (_, _) => Theme.Chrome(this, rounded: true);
        app.Changed += () => { if (Visible && Signature() != shown) Build(); };
    }

    static int Px(float px) => Theme.S(px);

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= Theme.Composited; return p; }
    }
    int Width_ => Px(360);
    int Inner => Width_ - stack.Padding.Horizontal;

    /// A click on the icon toggles it; the click that closed it by taking the
    /// focus away must not open it again.
    public void Toggle(Point near)
    {
        if (Visible) { Hide(); return; }
        if (DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(300)) return;
        anchor = near;
        Build();
        Show();
        Activate();
    }

    public void ShowForSnapshot(Point near) { anchor = near; Build(); Location = new Point(-4000, 0); Show(); }

    void Build()
    {
        Theme.Use(Theme.DpiAt(anchor));
        stack.Padding = new Padding(Px(14), Px(10), Px(14), Px(10));
        shown = Signature();
        SuspendLayout();
        stack.SuspendLayout();
        foreach (Control c in stack.Controls.Cast<Control>().ToList()) c.Dispose();
        stack.Controls.Clear();
        var now = DateTime.UtcNow;

        var top = new Strip(Px(34)) { Width = Inner };
        top.Left(new TextLabel(Theme.GSync, Theme.Icon, Theme.SubText), Px(8));
        top.Left(new TextLabel("Claude Desktop", Theme.BodyBold), Px(6));
        var waiting = app.Updates.ClaudeAvailable;
        var line = new TextLabel(app.Updates.ClaudeLine(), Theme.Small, waiting ? Theme.Red : Theme.Faint);
        if (waiting) { line.Cursor = Cursors.Hand; line.Click += async (_, _) => { Hide(); await app.Updates.UpdateClaude(null); }; }
        top.Left(line);
        var refresh = new FlatButton("", FlatButton.Kind.Quiet, Theme.GRefresh);
        tips.SetToolTip(refresh, S.T("Refresh usage"));
        refresh.Click += (_, _) => { app.Scan(); _ = app.RefreshUsage(Strength.Press); };
        top.Right(refresh, 0);
        stack.Controls.Add(top);
        Line();

        foreach (var entry in app.Entries())
        {
            var running = Entries.Running(entry, app.Snapshot);
            var head = new Strip(Px(36)) { Width = Inner };
            head.Left(new TextLabel("●", Theme.Body, running ? Theme.Green : Theme.Faint), Px(6));
            head.Left(new TextLabel(entry.Name, Theme.Title));
            var open = new FlatButton(S.T("Open"));
            open.Click += (_, _) => { Hide(); app.OpenEntry(entry); };
            head.Right(open, 0);
            stack.Controls.Add(head);

            var figure = app.Monitor.Entry(entry.ProfileDir);
            if (figure == null)
                stack.Controls.Add(new TextLabel(app.Monitor.Note(entry.ProfileDir) ?? S.T("no usage yet"), Theme.Small, Theme.SubText) { Margin = new Padding(Px(2), 0, 0, Px(6)) });
            else
            {
                if (figure.FiveHour is { } h) Bar(S.T("5 hours"), h, Countdown.Until(figure.FiveHourReset, now));
                Bar(S.T("Week"), figure.Week, Countdown.Until(figure.WeekReset, now));
                var age = now - figure.Taken;
                var when = age < TimeSpan.FromMinutes(1) ? S.T("just now") : S.F("{0} ago", Countdown.Until(now + age, now));
                stack.Controls.Add(new TextLabel(S.F(figure.Source == UsageFigure.Live ? "Updated {0} from Anthropic" : "Updated {0} from Claude's own record", when),
                    Theme.Small, Theme.Faint) { Margin = new Padding(Px(2), Px(2), 0, Px(6)) });
            }
            Line();
        }

        var conversations = app.Pool.Conversations();
        if (conversations.Count > 0)
        {
            stack.Controls.Add(new TextLabel(S.T("Shared conversations"), Theme.Small, Theme.Faint) { Margin = new Padding(Px(2), Px(2), 0, Px(4)) });
            foreach (var (session, title, choices) in conversations)
            {
                var row = new FlatButton(title.Length > 34 ? title[..34] + "…" : title, FlatButton.Kind.Quiet) { Chevron = true, AlignLeft = true, Width = Inner, Margin = new Padding(0) };
                row.Width = Inner;
                tips.SetToolTip(row, S.F("Answered by {0}. Choose another account for this conversation:", app.Pool.NameOf(session.ServedBy)));
                row.Click += (_, _) =>
                {
                    var menu = ThemedRenderer.Menu();
                    menu.ShowCheckMargin = true;
                    foreach (var choice in choices)
                    {
                        var option = new ToolStripMenuItem(app.Pool.NameOf(choice)) { Checked = choice == session.ServedBy };
                        option.Click += (_, _) => app.Pool.Assign(session.Id, choice);
                        menu.Items.Add(option);
                    }
                    menu.Show(row, new Point(0, row.Height));
                };
                stack.Controls.Add(row);
                stack.Controls.Add(new TextLabel(S.F("Answered by {0}", app.Pool.NameOf(session.ServedBy)), Theme.Small, Theme.SubText) { Margin = new Padding(Px(12), 0, 0, Px(4)) });
            }
            Line();
        }

        Switch(S.T("Start with Windows"), SafeStartup(), on => { try { Startup.Enabled = on; } catch { } });
        Switch(S.T("Read live usage from Anthropic"), app.Store.Data.LiveUsage, on =>
        {
            if (on != app.Store.Data.LiveUsage) app.ToggleLiveUsage();
            if (Visible) Build();
        });
        Line();

        Action(S.T("Refresh usage"), () => { app.Scan(); _ = app.RefreshUsage(Strength.Press); }, keepOpen: true);
        Action(S.T("Open Claude Graft"), app.ShowWindow);
        Action(S.T("Settings…"), app.ShowSettings);
        Action(S.T("Quit Claude Graft"), app.Quit);

        stack.ResumeLayout();
        var height = stack.Padding.Vertical + stack.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        var area = Screen.FromPoint(anchor).WorkingArea;
        height = Math.Min(height, area.Height - Px(16));
        stack.AutoScroll = height >= area.Height - Px(16);
        Size = new Size(Width_, height);
        var x = Math.Clamp(anchor.X - Width / 2, area.Left + Px(8), area.Right - Width - Px(8));
        var y = anchor.Y <= area.Top + Px(40) ? area.Top + Px(8) : area.Bottom - Height - Px(8);
        Location = new Point(x, y);
        ResumeLayout();
    }

    static bool SafeStartup() { try { return Startup.Enabled; } catch { return false; } }

    void Line() => stack.Controls.Add(new Panel { Height = 1, Width = Inner, BackColor = Theme.Line, Margin = new Padding(0, Px(4), 0, Px(4)) });

    void Bar(string label, int percent, string? reset)
    {
        stack.Controls.Add(new UsageRow { Label = label, Percent = percent, Compact = true, Width = Inner, Height = Px(22) });
        if (reset != null)
            stack.Controls.Add(new TextLabel(S.F("Resets in {0}", reset), Theme.Small, Theme.Faint) { Margin = new Padding(Px(64), 0, 0, Px(2)) });
    }

    void Switch(string label, bool on, Action<bool> changed)
    {
        var row = new Strip(Px(32)) { Width = Inner };
        row.Left(new TextLabel(label, Theme.Body), Px(2));
        var toggle = new Toggle { On = on, Size = new Size(Px(40), Px(22)) };
        toggle.Changed += (_, _) => changed(toggle.On);
        row.Right(toggle, 0);
        stack.Controls.Add(row);
    }

    void Action(string label, Action action, bool keepOpen = false)
    {
        var button = new FlatButton(label, FlatButton.Kind.Quiet) { AlignLeft = true, Width = Inner, Margin = new Padding(0) };
        button.Width = Inner;
        button.Click += (_, _) => { if (!keepOpen) Hide(); action(); };
        stack.Controls.Add(button);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// Settings, as switches in a card.
sealed class SettingsWindow : Form
{
    public SettingsWindow(TrayApp app)
    {
        Text = S.T("Claude Graft Settings");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        // Built for, and centred on, the screen the pointer is on.
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Theme.Use(Theme.DpiAt(new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2)));
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        static int Sc(float px) => Theme.S(px);
        ClientSize = new Size(Sc(460), Sc(400));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        Padding = new Padding(Sc(20));
        HandleCreated += (_, _) => Theme.Chrome(this);

        var card = new Card { Dock = DockStyle.Top, Padding = new Padding(Sc(16), Sc(6), Sc(16), Sc(6)) };
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        card.Controls.Add(rows);
        void Row(string label, bool on, Action<Toggle> changed, bool line = true)
        {
            var strip = new Strip(Sc(44)) { Width = Sc(388) };
            strip.Left(new TextLabel(label, Theme.Body));
            var toggle = new Toggle { On = on, Size = new Size(Sc(44), Sc(24)) };
            toggle.Changed += (_, _) => changed(toggle);
            strip.Right(toggle, 0);
            rows.Controls.Add(strip);
            if (line) rows.Controls.Add(new Panel { Height = 1, Width = Sc(388), BackColor = Theme.Line, Margin = Padding.Empty });
        }
        bool startup;
        try { startup = Startup.Enabled; } catch { startup = false; }
        Row(S.T("Start with Windows"), startup, t => { try { Startup.Enabled = t.On; } catch { t.On = !t.On; } });
        Row(S.T("Read live usage from Anthropic"), app.Store.Data.LiveUsage, t =>
        {
            if (t.On != app.Store.Data.LiveUsage) app.ToggleLiveUsage();
            t.On = app.Store.Data.LiveUsage;
        });
        // Claude never updates itself, and so never closes work in progress
        // to do it; updates are then installed from here.
        Row(S.T("Update Claude Desktop manually"), Core.Updates.ManualUpdates.Enabled, t =>
        {
            if (Core.Updates.ManualUpdates.SetEnabled(t.On) is { } problem)
            {
                t.On = Core.Updates.ManualUpdates.Enabled;
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (t.On) MessageBox.Show(this, S.T("Each Claude stops updating itself the next time it opens. Install new releases from the line at the bottom of Claude Graft's window."), Text);
        });
        Row(S.T("Hold Claude Graft updates"), Core.Updates.SelfUpdate.Held, t => Core.Updates.SelfUpdate.Held = t.On, line: false);
        card.Height = Sc(196);

        var note = new TextLabel(S.T("The icon in the notification area keeps reporting usage after this window is closed. Quit it from its own menu."),
            Theme.Small, Theme.SubText) { Dock = DockStyle.Top, AutoSize = false, Height = Sc(52), Padding = new Padding(Sc(4), Sc(12), 0, 0) };
        note.MaximumSize = new Size(Sc(410), 0);
        var about = new TextLabel($"Claude Graft for Windows {Core.Updates.SelfUpdate.Current} · MIT",
            Theme.Small, Theme.Faint) { Dock = DockStyle.Bottom, AutoSize = false, Height = Sc(24) };
        var graftUpdate = new FlatButton(app.Updates.Graft is { } r ? S.F("Install Claude Graft {0} and restart", r.Version) : S.T("Check for Claude Graft updates"),
                                         FlatButton.Kind.Quiet, Theme.GRefresh) { Dock = DockStyle.None };
        graftUpdate.Click += async (_, _) =>
        {
            graftUpdate.Enabled = false;
            if (app.Updates.Graft == null) await app.Updates.CheckGraft(installQuietly: false);
            if (app.Updates.Graft is { } release)
            {
                if (MessageBox.Show(this, S.F("Install Claude Graft {0} and restart it?", release.Version), Text, MessageBoxButtons.OKCancel) == DialogResult.OK
                    && await app.Updates.InstallGraft() is { } problem)
                    MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else MessageBox.Show(this, app.Updates.GraftProblem ?? S.F("Claude Graft {0} is the latest release.", Core.Updates.SelfUpdate.Current), Text);
            graftUpdate.Enabled = true;
        };
        var logs = new FlatButton(S.T("Open the log folder"), FlatButton.Kind.Quiet, Theme.GLog) { Dock = DockStyle.None };
        logs.Click += (_, _) => TrayApp.OpenFolder(Paths.GraftHome);
        var logRow = new Panel { Dock = DockStyle.Bottom, Height = Sc(72), BackColor = Color.Transparent };
        logRow.Controls.Add(logs);
        graftUpdate.Location = new Point(0, logs.Bottom + Sc(4));
        logRow.Controls.Add(graftUpdate);
        Controls.Add(note);
        Controls.Add(card);
        Controls.Add(logRow);
        Controls.Add(about);
    }
}
