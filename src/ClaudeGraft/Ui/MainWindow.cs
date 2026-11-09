using ClaudeGraft.Core;
using ClaudeGraft.Core.Pool;
using Microsoft.VisualBasic.FileIO;

namespace ClaudeGraft.Ui;

/// The window, laid out the way the macOS original's is: every Claude in a
/// sidebar, and the one selected on the right in cards (who it is and whose
/// chats it reads, its plan usage, the accounts it may borrow from), with
/// what can be done to it along the bottom.
sealed class MainWindow : Form
{
    const string DraftKey = "\u0000draft";
    const string ArtifactsKey = "#artifacts", ProjectsKey = "#projects", SearchKey = "#search";

    /// The claude.ai pages, kept while the window lives so what they found
    /// is still there when you come back to them.
    readonly Dictionary<string, WebPage> pages = [];

    sealed class Draft
    {
        public string Name = "";
        public string Folder = "";
        public bool FolderEdited;
        public string? Source;
        public string Color = "#C2410C";
    }

    readonly TrayApp app;
    readonly Panel sidebar = new() { Dock = DockStyle.Left };
    readonly FlowLayoutPanel list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly Panel footer = new() { Dock = DockStyle.Bottom };
    readonly Panel header = new() { Dock = DockStyle.Top };
    readonly Panel bottom = new() { Dock = DockStyle.Bottom };
    readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly ToolTip tips = new() { InitialDelay = 300, AutoPopDelay = 20000 };
    readonly System.Windows.Forms.Timer clock = new() { Interval = 30_000 };

    string selected = "";

    // What each part of the window shows now. A part is redrawn only when
    // what it would show differs: rebuilding everything on every change (a
    // usage refresh, a relay check every thirty seconds, a Claude coming to
    // the front) was what made the window flash.
    string shownKey = "\u0001none", sidebarSig = "", headerSig = "", usageSig = "", sharedSig = "";
    TextLabel? headerState;
    Draft? draft;

    // What the person has typed or chosen and not saved yet.
    Field? nameField, folderField;
    string? pendingSource;
    bool sourceTouched;
    Card? usageCard, sharedCard;
    TextLabel? usageSource, sharedState;

    public MainWindow(TrayApp app)
    {
        this.app = app;
        Text = "Claude Graft";
        AutoScaleMode = AutoScaleMode.None;
        // Built for the screen it opens on: the one holding the pointer.
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Theme.Use(Theme.DpiAt(new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2)));
        StartPosition = FormStartPosition.Manual;
        Font = Theme.Body;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Icon = IconMaker.TrayIcon(IconMaker.Parse(app.Store.Data.MainColor), "C", false);
        Size = new Size(S(900), S(640));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        Measure();
        Theme.Scrolling(list);
        Theme.Scrolling(body);

        sidebar.BackColor = Theme.Sidebar;
        footer.BackColor = Theme.Sidebar;
        list.BackColor = Theme.Sidebar;
        sidebar.Controls.Add(list);
        sidebar.Controls.Add(footer);

        header.BackColor = Theme.Window;
        bottom.BackColor = Theme.Window;
        body.BackColor = Theme.Window;
        body.Resize += (_, _) => FitWidths();

        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(bottom);
        Controls.Add(sidebar);

        list.Resize += (_, _) => { foreach (Control c in list.Controls) c.Width = list.ClientSize.Width - S(2); };
        app.Changed += OnChanged;
        clock.Tick += (_, _) => RefreshLive();
        clock.Start();
        FormClosed += (_, _) => { app.Changed -= OnChanged; clock.Dispose(); tips.Dispose(); };
        HandleCreated += (_, _) => Theme.Chrome(this);

        BuildSidebar();
        BuildFooter();
        Show(selected);
    }

    static int S(float px) => Theme.S(px);

    /// Every size that depends on the screen's scale.
    void Measure()
    {
        MinimumSize = new Size(S(840), S(580));
        sidebar.Width = S(250);
        sidebar.Padding = new Padding(S(10), S(14), S(10), 0);
        footer.Height = S(92);
        header.Height = S(62);
        header.Padding = new Padding(S(22), S(10), S(16), 0);
        bottom.Height = S(58);
        bottom.Padding = new Padding(S(18), S(12), S(18), S(14));
        body.Padding = new Padding(S(20), S(6), S(20), S(10));
    }

    /// Moved to a screen at another scale (a laptop at 125% beside a monitor
    /// at 100%): Windows resizes the frame, and everything inside is built
    /// again for the new scale rather than left at the old one, which spilled
    /// out of the window and brought up scrollbars.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rebuild(e.DeviceDpiNew);
    }

    void Rebuild(int dpi)
    {
        Theme.Use(dpi);
        SuspendLayout();
        Measure();
        // The pages were built for the old scale; they are made again.
        foreach (var p in pages.Values) { Controls.Remove(p); p.Dispose(); }
        pages.Clear();
        sidebarSig = "";
        BuildSidebar();
        BuildFooter();
        shownKey = "\u0001none";
        Show(selected);
        ResumeLayout(true);
    }

    /// Anything built from here on is for this window's screen, whatever the
    /// dropdown on another screen last asked for.
    void Mine()
    {
        if (IsHandleCreated && DeviceDpi != Theme.Dpi) Theme.Use(DeviceDpi);
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= Theme.Composited; return p; }
    }

    string footerLine = "";

    void OnChanged()
    {
        if (IsDisposed) return;
        Mine();
        if (app.Updates.ClaudeLine() != footerLine) { footerLine = app.Updates.ClaudeLine(); BuildFooter(); }
        BuildSidebar();
        if (selected != shownKey) Show(selected); else RefreshLive();
    }

    bool Dirty =>
        selected == DraftKey ||
        (Current?.Shortcut is { } s && ((nameField != null && nameField.Text.Trim() != s.Name) ||
                                        (sourceTouched && !string.Equals(pendingSource, s.Source, StringComparison.OrdinalIgnoreCase))));

    ClaudeEntry? Current => app.Entries().FirstOrDefault(e => e.Key == selected);

    string SourceName(string? source) =>
        string.IsNullOrEmpty(source) ? S_("Its own chats")
        : source.Equals(Graft.MainSource, StringComparison.OrdinalIgnoreCase) ? S_("Claude (main)")
        : app.Store.Find(source)?.Name ?? S_("Its own chats");

    static string S_(string text) => Core.S.T(text);

    // ======================================================================
    // Sidebar
    // ======================================================================

    string SidebarSignature()
    {
        var snapshot = app.Snapshot;
        var parts = new List<string> { Entries.Running(app.Entries()[0], snapshot).ToString() };
        foreach (var s in app.Store.Data.Shortcuts)
            parts.Add($"{s.Folder}|{s.Name}|{s.Color}|{s.Source}|{ClaudeProcesses.IsRunning(s.ProfileDir, snapshot)}");
        parts.Add(draft == null ? "-" : $"draft|{draft.Name}|{draft.Color}");
        return string.Join("\n", parts);
    }

    void BuildSidebar()
    {
        var signature = SidebarSignature();
        if (signature == sidebarSig && list.Controls.Count > 0)
        {
            foreach (var item in list.Controls.OfType<SidebarItem>())
            {
                var on = (string)item.Tag2! == selected;
                if (item.Selected != on) { item.Selected = on; item.Invalidate(); }
            }
            return;
        }
        sidebarSig = signature;
        list.SuspendLayout();
        foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
        list.Controls.Clear();
        var snapshot = app.Snapshot;

        Section("Claude");
        var main = app.Entries()[0];
        var mainRunning = Entries.Running(main, snapshot);
        AddItem(new SidebarItem
        {
            Title = S_("Claude"), Glyph = Theme.GHome,
            Subtitle = mainRunning ? S_("Open now") : S_("Installed normally"),
            Selected = selected == "", Tag2 = "",
        });
        foreach (var (key, title, glyph) in new[] {
                     (ArtifactsKey, S_("Artifacts"), Theme.GLibrary),
                     (ProjectsKey, S_("Projects"), Theme.GProjects),
                     (SearchKey, S_("Search Chats"), Theme.GSearch) })
            AddItem(new SidebarItem { Title = title, Glyph = glyph, Subtitle = S_("Every account"), Selected = selected == key, Tag2 = key });

        Section(S_("Other Claudes"));
        foreach (var s in app.Store.Data.Shortcuts)
        {
            var key = Paths.Normalize(s.ProfileDir);
            var running = ClaudeProcesses.IsRunning(s.ProfileDir, snapshot);
            AddItem(new SidebarItem
            {
                Title = s.Name, Swatch = IconMaker.Parse(s.Color), SwatchMark = IconMaker.Mark(s.Name),
                Subtitle = Graft.SourceDir(s, app.Store) == null ? S_("Its own chats") : Core.S.F("Chats of {0}", SourceName(s.Source)),
                Running = running, Selected = selected == key, Tag2 = key,
            });
        }
        if (draft != null)
            AddItem(new SidebarItem
            {
                Title = string.IsNullOrWhiteSpace(draft.Name) ? S_("New Claude") : draft.Name,
                Swatch = IconMaker.Parse(draft.Color), SwatchMark = IconMaker.Mark(draft.Name),
                Subtitle = S_("Not created yet"), Selected = selected == DraftKey, Tag2 = DraftKey,
            });
        else
            AddItem(new SidebarItem { Title = S_("New Claude"), Glyph = Theme.GAdd, Tag2 = "new" });
        list.ResumeLayout();
    }

    void Section(string title)
    {
        var label = new TextLabel(title, Theme.Small, Theme.Faint) { Margin = new Padding(S(6), S(list.Controls.Count == 0 ? 0 : 14), 0, S(4)) };
        list.Controls.Add(label);
    }

    void AddItem(SidebarItem item)
    {
        item.Width = list.ClientSize.Width - S(2);
        item.Height = S(string.IsNullOrEmpty(item.Subtitle) ? 38 : 50);
        item.Click += (_, _) =>
        {
            var key = (string)item.Tag2!;
            if (key == "new") NewClaude();
            else Select(key);
        };
        list.Controls.Add(item);
    }

    public void SelectForSnapshot(string key) { selected = key; BuildSidebar(); Show(key); }

    void Select(string key)
    {
        if (key == selected) return;
        Mine();
        if (selected != DraftKey && Dirty &&
            MessageBox.Show(this, S_("Discard the changes you have not saved?"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        selected = key;
        BuildSidebar();
        Show(key);
    }

    void BuildFooter()
    {
        footer.Controls.Clear();
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
        // Red only for a release waiting to be installed, the one state that
        // asks somebody to act; a click on it starts the update.
        var available = app.Updates.ClaudeAvailable;
        var desktop = new Strip(S(46)) { Dock = DockStyle.Top, Padding = new Padding(S(16), 0, S(10), 0) };
        var glyph = new TextLabel(Theme.GSync, Theme.Icon, available ? Theme.Red : Theme.SubText);
        var words = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, WrapContents = false };
        words.Controls.Add(new TextLabel("Claude Desktop", Theme.BodyBold));
        words.Controls.Add(new TextLabel(app.Updates.ClaudeLine(), Theme.Small, available ? Theme.Red : Theme.SubText));
        desktop.Left(glyph, S(10)).Left(words);
        var check = new FlatButton("", FlatButton.Kind.Quiet, Theme.GRefresh);
        tips.SetToolTip(check, available ? S_("Update Claude…") : S_("Check for a new Claude"));
        check.Click += async (_, _) => { if (app.Updates.ClaudeAvailable) await app.Updates.UpdateClaude(this); else await app.Updates.CheckClaude(); };
        desktop.Right(check, 0);
        foreach (Control c in new Control[] { glyph, words, desktop })
            if (available) { c.Cursor = Cursors.Hand; c.Click += async (_, _) => await app.Updates.UpdateClaude(this); }
        var links = new Strip(S(40)) { Dock = DockStyle.Bottom, Padding = new Padding(S(8), 0, S(8), S(4)) };
        var settings = new FlatButton(S_("Settings"), FlatButton.Kind.Quiet, Theme.GSettings);
        settings.Click += (_, _) => app.ShowSettings();
        var logs = new FlatButton(S_("Logs"), FlatButton.Kind.Quiet, Theme.GLog);
        logs.Click += (_, _) => TrayApp.OpenFolder(Paths.GraftHome);
        links.Left(settings, 0).Right(logs, 0);
        footer.Controls.Add(desktop);
        footer.Controls.Add(links);
        footer.Controls.Add(line);
    }

    // ======================================================================
    // The selected Claude
    // ======================================================================

    public void NewClaude()
    {
        if (draft == null)
        {
            var name = DefaultName();
            draft = new Draft
            {
                Name = name,
                Folder = ProfileName.Unique(ProfileName.Suggest(name), app.Store.Folders),
                Color = app.Store.NextColor(),
            };
        }
        selected = DraftKey;
        BuildSidebar();
        Show(DraftKey);
    }

    string DefaultName()
    {
        for (var n = 2; ; n++)
        {
            var name = $"Claude {n}";
            if (!app.Store.Data.Shortcuts.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    void ShowPage(string key)
    {
        SuspendLayout();
        shownKey = key;
        headerState = null;
        body.Visible = bottom.Visible = false;
        foreach (var other in pages.Where(p => p.Key != key)) other.Value.Visible = false;
        if (!pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                ArtifactsKey => new ArtifactsPage(app),
                ProjectsKey => new ProjectsPage(app),
                _ => new SearchPage(app),
            };
            page.Dock = DockStyle.Fill;
            pages[key] = page;
            Controls.Add(page);
            // Where the content sits in the docking order, so the header and
            // the sidebar keep their places.
            Controls.SetChildIndex(page, Controls.GetChildIndex(body));
        }
        page.Visible = true;
        var title = key switch { ArtifactsKey => S_("Artifacts"), ProjectsKey => S_("Projects"), _ => S_("Search Chats") };
        var count = app.Web.Accounts.Count;
        PlainHeader(title + " · " + (count == 0 ? S_("no account signed in") : Core.S.F(count == 1 ? "{0} account" : "{0} accounts", count)));
        ResumeLayout(true);
        page.Shown();
    }

    void PlainHeader(string state)
    {
        header.Controls.Clear();
        var words = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Dock = DockStyle.Left };
        words.Controls.Add(new TextLabel("Claude Graft", Theme.Title));
        words.Controls.Add(new TextLabel(state, Theme.Small, Theme.SubText));
        header.Controls.Add(words);
    }

    void Show(string key)
    {
        if (key.StartsWith('#')) { ShowPage(key); return; }
        foreach (var p in pages.Values) p.Visible = false;
        body.Visible = bottom.Visible = true;
        SuspendLayout();
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear();
        nameField = folderField = null;
        usageCard = sharedCard = null;
        usageSource = sharedState = null;
        headerState = null;
        sourceTouched = false;
        shownKey = key;
        headerSig = usageSig = sharedSig = "";

        var entry = key == DraftKey ? null : app.Entries().FirstOrDefault(e => e.Key == key);
        if (key != DraftKey && entry == null) { selected = ""; entry = app.Entries()[0]; }
        pendingSource = key == DraftKey ? draft!.Source : entry!.Shortcut?.Source;

        BuildHeader(entry);
        body.Controls.Add(IdentityCard(entry));
        if (entry?.Shortcut is { } s && Graft.SourceDir(s, app.Store) == null && ElsewhereCard(s) is { } elsewhere)
            body.Controls.Add(elsewhere);
        if (entry != null)
        {
            body.Controls.Add(SectionTitle(S_("Plan usage"), S_("The five-hour and weekly windows of this Claude's account. Live from Anthropic when that is turned on in Settings, otherwise from what this Claude records while it is open."),
                out usageSource, RefreshButton()));
            usageCard = new Card();
            body.Controls.Add(usageCard);
            FillUsage(entry);

            var add = new FlatButton(S_("Add another account"), FlatButton.Kind.Quiet, Theme.GAdd) { Chevron = true };
            add.AutoSizeNow();
            add.Click += (_, _) => AddLenderMenu(entry).Show(add, new Point(0, add.Height));
            body.Controls.Add(SectionTitle(S_("Shared usage"), S_("When this Claude's account runs out, its Claude Code carries on with the allowance of the accounts added here. Only the model's answers come from them: chats, files and connectors stay with this Claude. The Chat tab is never shared."),
                out sharedState, add));
            sharedCard = new Card();
            body.Controls.Add(sharedCard);
            FillShared(entry);
        }
        BuildBottom(entry);
        FitWidths();
        body.ResumeLayout();
        ResumeLayout();
    }

    void FitWidths()
    {
        var width = body.ClientSize.Width - body.Padding.Horizontal - (body.VerticalScroll.Visible ? 0 : 0);
        foreach (Control c in body.Controls) c.Width = Math.Max(S(300), width);
    }

    void BuildHeader(ClaudeEntry? entry)
    {
        var running = entry != null && Entries.Running(entry, app.Snapshot);
        var state = entry == null ? S_("Not created yet") + " · " + S_("Not open")
                  : running ? S_("Open now") : S_("Not open");
        if (entry?.Shortcut is { } s && Graft.SourceDir(s, app.Store) != null) state += " · " + Core.S.F("reads the chats of {0}", SourceName(s.Source));
        if (headerState != null)
        {
            if (headerSig != state) { headerSig = state; headerState.Text = state; }
            return;
        }
        headerSig = state;
        header.Controls.Clear();
        var words = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Dock = DockStyle.Left };
        words.Controls.Add(new TextLabel("Claude Graft", Theme.Title));
        headerState = new TextLabel(state, Theme.Small, Theme.SubText);
        words.Controls.Add(headerState);
        var plus = new FlatButton("", FlatButton.Kind.Secondary, Theme.GAdd) { Dock = DockStyle.Right };
        plus.Size = new Size(S(36), S(36));
        tips.SetToolTip(plus, S_("New Claude"));
        plus.Click += (_, _) => NewClaude();
        var holder = new Panel { Dock = DockStyle.Right, Width = S(40), BackColor = Color.Transparent };
        plus.Location = new Point(0, S(2));
        plus.Dock = DockStyle.None;
        holder.Controls.Add(plus);
        header.Controls.Add(words);
        header.Controls.Add(holder);
    }

    Control SectionTitle(string title, string info, out TextLabel side, Control? right)
    {
        var strip = new Strip(S(40)) { Margin = new Padding(0, S(14), 0, S(4)) };
        strip.Left(new TextLabel(title, Theme.Heading), S(6));
        var i = new TextLabel(Theme.GInfo, Theme.IconSmall, Theme.SubText) { Cursor = Cursors.Help };
        tips.SetToolTip(i, info);
        strip.Left(i, S(14));
        side = new TextLabel("", Theme.Small, Theme.Faint);
        strip.Left(side);
        if (right != null) strip.Right(right, 0);
        return strip;
    }

    FlatButton RefreshButton()
    {
        var refresh = new FlatButton("", FlatButton.Kind.Quiet, Theme.GRefresh);
        tips.SetToolTip(refresh, S_("Refresh usage"));
        refresh.Click += (_, _) => { app.Scan(); _ = app.RefreshUsage(Strength.Press); };
        return refresh;
    }

    // ---- Identity ----

    Card IdentityCard(ClaudeEntry? entry)
    {
        var card = new Card { Padding = new Padding(S(12), S(8), S(12), S(8)) };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        card.Controls.Add(stack);
        var top = new Strip(S(40));

        var isMain = entry != null && entry.IsMain;
        var color = entry?.Color ?? draft!.Color;
        var name = entry?.Name ?? draft!.Name;
        if (isMain)
        {
            top.Left(new TextLabel(Theme.GHome, Theme.IconLarge, Theme.Text), S(12));
            top.Left(new TextLabel(S_("Claude"), Theme.Title), S(8));
            var i = new TextLabel(Theme.GInfo, Theme.IconSmall, Theme.SubText) { Cursor = Cursors.Help };
            tips.SetToolTip(i, S_("Claude's own profile, as Claude opens it normally. Claude Graft opens it and reads its usage, and never moves, renames or deletes it."));
            top.Left(i);
        }
        else
        {
            var swatch = new PictureBox { Size = new Size(S(26), S(26)), BackColor = Color.Transparent, Image = IconMaker.Draw(S(26), IconMaker.Parse(color), IconMaker.Mark(name)) };
            top.Left(swatch, S(10));
            nameField = new Field(name) { Width = S(250) };
            nameField.Box.TextChanged += (_, _) => OnNameTyped();
            top.Left(nameField);
        }

        var folderText = entry == null ? draft!.Folder : entry.Shortcut?.Folder ?? (entry.ProfileDir != null ? Path.GetFileName(entry.ProfileDir) : "—");
        if (!isMain)
        {
            var more = new FlatButton("", FlatButton.Kind.Quiet, Theme.GMore);
            tips.SetToolTip(more, S_("More"));
            more.Click += (_, _) => MoreMenu(entry).Show(more, new Point(0, more.Height));
            top.Right(more, S(2));
        }
        var folderButton = new FlatButton("", FlatButton.Kind.Quiet, Theme.GFolder) { Enabled = entry?.ProfileDir != null };
        tips.SetToolTip(folderButton, S_("Show folder"));
        folderButton.Click += (_, _) => { if (entry?.ProfileDir != null) TrayApp.OpenFolder(entry.ProfileDir); };
        top.Right(folderButton, S(2));
        folderField = new Field(folderText, Theme.Mono, readOnly: entry != null) { Width = S(200) };
        if (entry == null) folderField.Box.TextChanged += (_, _) => { draft!.Folder = folderField.Text.Trim(); draft.FolderEdited = folderField.Box.Focused; ValidateDraft(); };
        tips.SetToolTip(folderField.Box, S_("Profile folder"));
        top.Right(folderField, S(4));
        stack.Controls.Add(top);

        if (!isMain)
        {
            stack.Controls.Add(new Panel { Height = 1, BackColor = Theme.Line, Margin = new Padding(0, S(4), 0, S(4)) });
            var chats = new Strip(S(36));
            chats.Left(new TextLabel(S_("Reads chats from"), Theme.Body), S(6));
            var i = new TextLabel(Theme.GInfo, Theme.IconSmall, Theme.SubText) { Cursor = Cursors.Help };
            tips.SetToolTip(i, S_("Borrowing gives this Claude a copy of the other's chats, kept in step every time either opens. Its own chats are added to the other's too, and stay there if you go back: that merge cannot be undone. Going back returns this Claude to exactly its own chats."));
            chats.Left(i);
            var picker = new FlatButton(SourceName(pendingSource), FlatButton.Kind.Quiet) { Chevron = true };
            picker.AutoSizeNow();
            picker.Click += (_, _) => SourceMenu(entry?.Shortcut, picker).Show(picker, new Point(0, picker.Height));
            chats.Right(picker, 0);
            stack.Controls.Add(chats);
            var note = new TextLabel("", Theme.Small, Theme.SubText) { Margin = new Padding(0, 0, 0, S(2)) };
            note.Name = "sourceNote";
            stack.Controls.Add(note);
            UpdateSourceNote(note);
        }
        stack.Resize += (_, _) => { foreach (Control c in stack.Controls) if (c is Strip or Panel) c.Width = stack.ClientSize.Width; };
        card.Height = isMain ? S(58) : S(132);
        return card;
    }

    void UpdateSourceNote(TextLabel? note = null)
    {
        note ??= body.Controls.Find("sourceNote", true).FirstOrDefault() as TextLabel;
        if (note == null) return;
        var own = string.IsNullOrEmpty(pendingSource) || SourceName(pendingSource) == S_("Its own chats");
        note.Text = own ? S_("Only this Claude's own chats.") : Core.S.F("Shared both ways with {0}.", SourceName(pendingSource));
        if (!own && Current?.Shortcut is { } s && Core.Sidebar.SidebarSync.Status(s.ProfileDir) is { } pins) note.Text += "  " + pins;
    }

    void OnNameTyped()
    {
        if (selected == DraftKey && draft != null && nameField != null)
        {
            draft.Name = nameField.Text;
            if (!draft.FolderEdited && folderField != null)
                folderField.Text = ProfileName.Unique(ProfileName.Suggest(nameField.Text), app.Store.Folders);
            ValidateDraft();
        }
        UpdateBottomState();
    }

    ContextMenuStrip SourceMenu(Shortcut? s, FlatButton picker)
    {
        var menu = ThemedRenderer.Menu();
        menu.ShowCheckMargin = true;
        var self = s?.Folder;
        var lending = self != null && app.Store.Data.Shortcuts.Any(o => string.Equals(o.Source, self, StringComparison.OrdinalIgnoreCase));
        void Option(string label, string? value, bool enabled = true)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = string.Equals(value ?? "", pendingSource ?? "", StringComparison.OrdinalIgnoreCase),
                Enabled = enabled,
            };
            item.Click += (_, _) =>
            {
                pendingSource = value;
                sourceTouched = true;
                if (selected == DraftKey && draft != null) draft.Source = value;
                picker.Text = SourceName(value);
                UpdateSourceNote();
                UpdateBottomState();
            };
            menu.Items.Add(item);
        }
        Option(S_("Its own chats"), null);
        menu.Items.Add(new ToolStripSeparator());
        Option(S_("Claude (main)"), Graft.MainSource, !lending && Paths.MainProfile != null);
        foreach (var other in app.Store.Data.Shortcuts.Where(o => o.Folder != self && string.IsNullOrEmpty(o.Source)))
            Option(other.Name, other.Folder, !lending);
        if (lending)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(S_("Another Claude reads this one's chats, so this one keeps its own.")) { Enabled = false });
        }
        return menu;
    }

    ContextMenuStrip MoreMenu(ClaudeEntry? entry)
    {
        var menu = ThemedRenderer.Menu();
        var s = entry?.Shortcut;
        void Item(string label, Action action, bool enabled = true)
        {
            var item = new ToolStripMenuItem(label) { Enabled = enabled };
            item.Click += (_, _) => Try(action);
            menu.Items.Add(item);
        }
        Item(S_("Desktop shortcut"), () => MakeLink(s!, ShellLinks.DesktopDir), s != null);
        Item(S_("Start menu shortcut"), () => MakeLink(s!, ShellLinks.StartDir), s != null);
        Item(S_("Copy MCP servers from main"), () => CopyServers(entry!), s != null);
        return menu;
    }

    Card? ElsewhereCard(Shortcut s)
    {
        var everyone = new List<string>();
        if (Paths.MainProfile is { } m) everyone.Add(m);
        everyone.AddRange(app.Store.Data.Shortcuts.Where(o => o != s).Select(o => o.ProfileDir));
        Graft.ChatsElsewhereInfo? found;
        try { found = Graft.ChatsElsewhere(s.ProfileDir, everyone); } catch { found = null; }
        if (found == null) return null;
        var where = Paths.Same(found.Profile, Paths.MainProfile) ? S_("Claude (main)")
            : app.Store.Data.Shortcuts.FirstOrDefault(o => Paths.Same(o.ProfileDir, found.Profile))?.Name ?? found.Profile;
        var card = new Card { Margin = new Padding(0, S(10), 0, 0) };
        var text = Core.S.F(found.Merging
            ? "{0} more chats of this account are in {1}. Copying them adds them to the ones here:"
            : "This Claude's account has {0} chats in {1}, left there when it signed in over there:", found.Count, where);
        var titles = string.Join("   ·   ", found.Chats.Take(3).Select(c => c.Title));
        var copy = new FlatButton(Core.S.F("Copy them from {0}", where));
        copy.Click += (_, _) =>
        {
            var result = Graft.AdoptChats(found.Profile, s.ProfileDir, found.Account, everyone.Append(s.ProfileDir));
            if (result.Running.Count > 0)
            {
                MessageBox.Show(this, S_("Close every Claude first: one that is running builds its list of chats as it starts and may write over records copied underneath it."),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            copy.Enabled = false;
            copy.Text = Core.S.F("{0} chats copied.", result.Copied);
        };
        var words = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        words.Controls.Add(new TextLabel(text, Theme.Body) { MaximumSize = new Size(S(470), 0) });
        words.Controls.Add(new TextLabel(titles, Theme.Small, Theme.SubText) { MaximumSize = new Size(S(470), 0), Margin = new Padding(0, S(4), 0, 0) });
        var side = new Panel { Dock = DockStyle.Right, Width = copy.Width + S(4), BackColor = Color.Transparent };
        copy.Location = new Point(S(4), S(8));
        side.Controls.Add(copy);
        card.Controls.Add(words);
        card.Controls.Add(side);
        card.Height = S(78);
        return card;
    }

    // ---- Usage ----

    void FillUsage(ClaudeEntry entry)
    {
        if (usageCard == null) return;
        var now = DateTime.UtcNow;
        var figure = app.Monitor.Entry(entry.ProfileDir);
        var note = app.Monitor.Note(entry.ProfileDir);
        if (usageSource != null)
        {
            var source = figure == null ? "" :
                Core.S.F(figure.Source == UsageFigure.Live ? "Updated {0} from Anthropic" : "Updated {0} from Claude's own record", Ago(figure.Taken, now)) +
                (figure.IsStale(now) ? " " + S_("(old figure)") : "");
            if (usageSource.Text != source) usageSource.Text = source;
        }
        var signature = figure == null ? "none|" + note
            : $"{figure.FiveHour}|{figure.Week}|{Countdown.Until(figure.FiveHourReset, now)}|{Countdown.Until(figure.WeekReset, now)}|{note}";
        if (signature == usageSig && usageCard.Controls.Count > 0) return;
        usageSig = signature;
        usageCard.SuspendLayout();
        usageCard.Controls.Clear();
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        usageCard.Controls.Add(rows);
        if (figure == null)
        {
            rows.Controls.Add(new TextLabel(note ?? S_("No usage reported yet"), Theme.Body, Theme.SubText) { Margin = new Padding(0, S(4), 0, 0) });
            usageCard.Height = S(44);
            usageCard.ResumeLayout();
            return;
        }
        if (figure.FiveHour is { } h)
            rows.Controls.Add(new UsageRow { Label = S_("5 hours"), Percent = h, Reset = Countdown.Until(figure.FiveHourReset, now) ?? "", Height = S(26) });
        rows.Controls.Add(new UsageRow { Label = S_("Week"), Percent = figure.Week, Reset = Countdown.Until(figure.WeekReset, now) ?? "", Height = S(26) });
        if (note != null) rows.Controls.Add(new TextLabel(note, Theme.Small, Theme.Amber) { Margin = new Padding(0, S(4), 0, 0) });
        rows.Resize += (_, _) => { foreach (Control c in rows.Controls) if (c is UsageRow) c.Width = rows.ClientSize.Width; };
        usageCard.Height = S(20) + rows.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        usageCard.ResumeLayout();
    }

    static string Ago(DateTime when, DateTime now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1)) return S_("just now");
        return Core.S.F("{0} ago", Countdown.Until(now + age, now) ?? "");
    }

    // ---- Shared usage ----

    void FillShared(ClaudeEntry entry)
    {
        if (sharedCard == null) return;
        var id = SharedUsage.IdOf(entry.Shortcut);
        var pool = app.Pool;
        var lenders = pool.Settings.LendersOf(id);
        var stateText = lenders.Count > 0 ? pool.StateText(id) : "";
        if (sharedState != null && sharedState.Text != stateText) sharedState.Text = stateText;
        var signature = string.Join("\n", lenders.Select(l =>
        {
            var m = pool.Status?.Members.FirstOrDefault(x => x.Id == l);
            var profile = LenderLook(l).Profile;
            var f = profile == null ? null : app.Monitor.Entry(profile);
            return $"{l}|{pool.NameOf(l)}|{m?.Served}|{m?.TokenRefused}|{f?.FiveHour}|{f?.Week}";
        })) + "|" + SafeStartup();
        if (signature == sharedSig && sharedCard.Controls.Count > 0) return;
        sharedSig = signature;
        sharedCard.Controls.Clear();
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        sharedCard.Controls.Add(rows);
        if (lenders.Count == 0)
        {
            rows.Controls.Add(new TextLabel(S_("No accounts added"), Theme.Body, Theme.SubText) { Margin = new Padding(0, S(4), 0, 0) });
            sharedCard.Height = S(44);
            return;
        }
        var now = DateTime.UtcNow;
        var first = true;
        foreach (var lender in lenders)
        {
            if (!first) rows.Controls.Add(new Panel { Height = 1, BackColor = Theme.Line, Margin = new Padding(0, S(2), 0, S(2)) });
            first = false;
            var row = new Strip(S(40));
            var (color, mark, profile) = LenderLook(lender);
            row.Left(new PictureBox { Size = new Size(S(20), S(20)), Image = IconMaker.Draw(S(20), color, mark), BackColor = Color.Transparent }, S(10));
            row.Left(new TextLabel(pool.NameOf(lender), Theme.BodyBold), S(10));
            var status = pool.Status?.Members.FirstOrDefault(m => m.Id == lender);
            var state = status == null ? "" : status.TokenRefused ? S_("its login was refused") : status.Served > 0 ? Core.S.F("answered {0} requests", status.Served) : "";
            if (state.Length > 0) row.Left(new TextLabel(state, Theme.Small, Theme.SubText));
            var remove = new FlatButton("", FlatButton.Kind.Quiet, Theme.GRemove);
            tips.SetToolTip(remove, S_("Stop borrowing from this account"));
            remove.Click += (_, _) => RemoveLender(id, lender);
            row.Right(remove, S(4));
            if (profile != null && app.Monitor.Entry(profile) is { } f)
                row.Right(new TextLabel(Entries.Short(f, now), Theme.Small, Theme.SubText), S(8));
            rows.Controls.Add(row);
        }
        if (!SafeStartup())
        {
            rows.Controls.Add(new Panel { Height = 1, BackColor = Theme.Line, Margin = new Padding(0, S(2), 0, S(2)) });
            var hint = new Strip(S(40));
            hint.Left(new TextLabel(S_("Starting with Windows keeps this working after a restart"), Theme.Body, Theme.SubText));
            var on = new FlatButton(S_("Turn on"));
            on.Click += (_, _) => { try { Startup.Enabled = true; } catch { } sharedSig = ""; FillShared(entry); FitWidths(); };
            hint.Right(on, 0);
            rows.Controls.Add(hint);
        }
        rows.Resize += (_, _) => { foreach (Control c in rows.Controls) c.Width = rows.ClientSize.Width; };
        sharedCard.Height = S(20) + rows.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
    }

    static bool SafeStartup() { try { return Startup.Enabled; } catch { return false; } }

    (Color Color, string Mark, string? Profile) LenderLook(string lender)
    {
        if (lender == SharedUsage.MainId) return (IconMaker.Parse(app.Store.Data.MainColor), "C", Paths.MainProfile);
        if (app.Store.Find(lender) is { } s) return (IconMaker.Parse(s.Color), IconMaker.Mark(s.Name), s.ProfileDir);
        return (Theme.Faint, "@", null);
    }

    ContextMenuStrip AddLenderMenu(ClaudeEntry entry)
    {
        var menu = ThemedRenderer.Menu();
        var id = SharedUsage.IdOf(entry.Shortcut);
        var pool = app.Pool;
        var already = pool.Settings.LendersOf(id).ToHashSet();
        var any = false;
        void Item(string label, string lender)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => AddLender(entry, lender);
            menu.Items.Add(item);
            any = true;
        }
        if (id != SharedUsage.MainId && !already.Contains(SharedUsage.MainId)) Item(S_("Claude (main)"), SharedUsage.MainId);
        foreach (var s in app.Store.Data.Shortcuts.Where(s => s.Folder != id && !already.Contains(s.Folder))) Item(s.Name, s.Folder);
        foreach (var a in pool.Settings.Accounts.Where(a => !already.Contains(a.Id))) Item(a.Name, a.Id);
        if (any) menu.Items.Add(new ToolStripSeparator());
        var signIn = new ToolStripMenuItem(S_("Sign in another account…"));
        signIn.Click += async (_, _) => await SignIn(entry);
        menu.Items.Add(signIn);
        return menu;
    }

    bool ConfirmSharing()
    {
        if (app.Pool.AnyBorrowing) return true;
        return MessageBox.Show(this, S_("Shared usage reads each Claude's saved login on this PC (with your Windows account's own protection) so the relay can recognise its requests and lend its allowance. Logins go to api.anthropic.com alone and are kept in the relay's memory, never written anywhere else. Turn it on?"),
                               S_("Shared usage"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void AddLender(ClaudeEntry entry, string lender)
    {
        if (!ConfirmSharing()) return;
        var pool = app.Pool;
        pool.Reload();
        var id = SharedUsage.IdOf(entry.Shortcut);
        var list = pool.Settings.LendersOf(id);
        if (!list.Contains(lender)) pool.Settings.Lending[id] = [.. list, lender];
        pool.Settings.Save();
        Diagnostics.Note("pool.lending", $"{id} +{lender}");
        pool.Tick();
        sharedSig = "";
        FillShared(entry);
        FitWidths();
    }

    void RemoveLender(string id, string lender)
    {
        var pool = app.Pool;
        pool.Reload();
        var list = pool.Settings.LendersOf(id);
        list.Remove(lender);
        if (list.Count == 0) pool.Settings.Lending.Remove(id); else pool.Settings.Lending[id] = list;
        // An account signed in only to lend, lent to nobody now, is forgotten.
        if (lender.StartsWith(SharedUsage.AccountPrefix) && !pool.Settings.Lending.Values.Any(l => l.Contains(lender)))
            pool.Settings.Accounts.RemoveAll(a => a.Id == lender);
        pool.Settings.Save();
        Diagnostics.Note("pool.lending", $"{id} -{lender}");
        pool.Tick();
        if (Current is { } e) { sharedSig = ""; FillShared(e); FitWidths(); }
    }

    async Task SignIn(ClaudeEntry entry)
    {
        if (!ConfirmSharing()) return;
        if (sharedState != null) sharedState.Text = S_("Finish signing in in your browser…");
        var own = entry.ProfileDir == null ? null : Graft.Account(entry.ProfileDir);
        var (account, problem) = await SharedUsage.SignInAsync(own, CancellationToken.None);
        if (IsDisposed) return;
        if (account == null)
        {
            if (sharedState != null) sharedState.Text = problem ?? "";
            return;
        }
        var pool = app.Pool;
        pool.Reload();
        pool.Settings.Accounts.RemoveAll(a => a.Id == account.Id);
        pool.Settings.Accounts.Add(account);
        pool.Settings.Save();
        AddLender(entry, account.Id);
    }

    // ---- Bottom bar ----

    FlatButton? primary, openButton;

    void BuildBottom(ClaudeEntry? entry)
    {
        bottom.Controls.Clear();
        var strip = new Strip(S(32)) { Dock = DockStyle.Fill };
        bottom.Controls.Add(strip);
        primary = null;
        if (entry == null)
        {
            var discard = new FlatButton(S_("Discard"));
            discard.Click += (_, _) => { draft = null; selected = ""; BuildSidebar(); Show(""); };
            strip.Left(discard, 0);
            primary = new FlatButton(S_("Create"), FlatButton.Kind.Primary);
            primary.Click += (_, _) => Try(Create);
            strip.Right(primary, 0);
            openButton = new FlatButton(S_("Open")) { Enabled = false };
            strip.Right(openButton);
            ValidateDraft();
            return;
        }
        if (entry.Shortcut != null)
        {
            var remove = new FlatButton(S_("Remove…"), FlatButton.Kind.Danger);
            remove.Click += (_, _) => Try(() => Remove(entry));
            strip.Left(remove, 0);
            primary = new FlatButton(S_("Save changes"), FlatButton.Kind.Primary);
            primary.Click += (_, _) => Try(() => Save(entry.Shortcut));
            strip.Right(primary, 0);
        }
        openButton = new FlatButton(S_("Open"), entry.Shortcut == null ? FlatButton.Kind.Primary : FlatButton.Kind.Secondary);
        openButton.Click += (_, _) => app.OpenEntry(entry);
        strip.Right(openButton, entry.Shortcut == null ? 0 : 6);
        UpdateBottomState();
    }

    void UpdateBottomState()
    {
        if (primary == null || selected == DraftKey) return;
        primary.Enabled = Dirty && !string.IsNullOrWhiteSpace(nameField?.Text);
    }

    void ValidateDraft()
    {
        if (draft == null || primary == null || selected != DraftKey) return;
        var problem = string.IsNullOrWhiteSpace(draft.Name) ? S_("Give the Claude a name.") : ProfileName.Validate(draft.Folder);
        if (problem == null && app.Store.Find(draft.Folder) != null) problem = Core.S.F("A Claude already uses the folder “{0}”.", draft.Folder);
        primary.Enabled = problem == null;
        tips.SetToolTip(primary, problem ?? "");
        if (folderField != null) folderField.Box.ForeColor = ProfileName.Validate(draft.Folder) == null ? Theme.Text : Theme.Red;
    }

    // ======================================================================
    // Actions
    // ======================================================================

    void Try(Action action)
    {
        try { action(); }
        catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    bool ConfirmMerge(string? from, string? to)
    {
        var borrowingNow = !string.IsNullOrEmpty(to);
        var wasOwn = string.IsNullOrEmpty(from);
        if (!(borrowingNow && wasOwn)) return true;
        return MessageBox.Show(this, S_("Borrowing gives this Claude a copy of the other's chats, kept in step every time either opens. Its own chats are added to the other's too, and stay there if you go back: that merge cannot be undone. Going back returns this Claude to exactly its own chats."),
                               S_("Reads chats from"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
    }

    void Create()
    {
        if (draft == null) return;
        var name = draft.Name.Trim();
        if (!ConfirmMerge(null, draft.Source)) return;
        var shortcut = app.Store.Add(name, draft.Folder);
        shortcut.Source = draft.Source;
        try
        {
            ShellLinks.Create(shortcut, ShellLinks.DesktopDir);
            ShellLinks.Create(shortcut, ShellLinks.StartDir);
        }
        catch (Exception e) { Diagnostics.Note("links.create-failed", e.Message); }
        if (!app.TrySave()) { app.Store.Data.Shortcuts.Remove(shortcut); return; }
        Diagnostics.Note("shortcut.added", shortcut.Folder);
        if (!string.IsNullOrEmpty(shortcut.Source)) { Graft.Apply(shortcut, app.Store); Graft.MirrorKnownPairs(); }
        draft = null;
        selected = Paths.Normalize(shortcut.ProfileDir);
        app.NotifyChanged();
    }

    void Save(Shortcut s)
    {
        var name = nameField?.Text.Trim() ?? s.Name;
        if (name.Length == 0) return;
        var sourceChanged = sourceTouched && !string.Equals(pendingSource, s.Source, StringComparison.OrdinalIgnoreCase);
        if (sourceChanged && !ConfirmMerge(s.Source, pendingSource)) return;
        var (oldName, oldSource) = (s.Name, s.Source);
        s.Name = name;
        if (sourceChanged) s.Source = pendingSource;
        if (!app.TrySave()) { s.Name = oldName; s.Source = oldSource; return; }
        if (name != oldName && s.Links.Count > 0) ShellLinks.Rename(s);
        if (sourceChanged)
        {
            Diagnostics.Note("shortcut.source", $"{s.Folder} -> {s.Source ?? "own"}");
            // Now when this Claude is closed; otherwise when it next opens,
            // since a running Claude holds its files and rewrites them.
            if (!ClaudeProcesses.IsRunning(s.ProfileDir))
            {
                UseWaitCursor = true;
                try { Graft.Apply(s, app.Store); Graft.MirrorKnownPairs(); }
                finally { UseWaitCursor = false; }
            }
            else MessageBox.Show(this, Core.S.F("{0} will read {1} the next time it opens.", s.Name, SourceName(s.Source)), Text,
                                 MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        sourceTouched = false;
        app.NotifyChanged();
    }

    void MakeLink(Shortcut s, string directory)
    {
        var path = ShellLinks.Create(s, directory);
        if (app.TrySave()) MessageBox.Show(this, Core.S.F("Shortcut created: {0}", path), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void CopyServers(ClaudeEntry entry)
    {
        if (Entries.Running(entry, ClaudeProcesses.Snapshot())) { MessageBox.Show(this, Core.S.F("Close {0} first.", entry.Name), Text); return; }
        var main = Paths.MainProfile;
        if (main == null) { MessageBox.Show(this, S_("There is no main Claude profile on this PC yet."), Text); return; }
        var added = DesktopServers.CopyMissing(main, entry.Shortcut!.ProfileDir);
        MessageBox.Show(this, added == 0 ? S_("Nothing to copy: this Claude already has every server the main one has.")
                                         : Core.S.F("{0} MCP servers copied.", added), Text);
    }

    void Remove(ClaudeEntry entry)
    {
        var s = entry.Shortcut!;
        if (Entries.Running(entry, ClaudeProcesses.Snapshot())) { MessageBox.Show(this, Core.S.F("Close {0} first.", s.Name), Text); return; }
        using var confirm = new RemoveDialog(s.Name);
        if (confirm.ShowDialog(this) != DialogResult.OK) return;

        ShellLinks.Delete(s);
        // A folder that stays goes back to its own chats first, handing over
        // what it did while borrowing; either way its pairs are forgotten.
        if (confirm.KeepFolder && Directory.Exists(s.ProfileDir)) Graft.Ungraft(s.ProfileDir);
        Graft.ForgetMirrors(s.ProfileDir);
        if (!confirm.KeepFolder && Directory.Exists(s.ProfileDir))
        {
            if (!Paths.IsOwnProfile(s.ProfileDir)) throw new InvalidOperationException(S_("That profile folder is not one of Claude Graft's."));
            FileSystem.DeleteDirectory(s.ProfileDir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        app.Store.Data.Shortcuts.Remove(s);
        foreach (var other in app.Store.Data.Shortcuts.Where(o => string.Equals(o.Source, s.Folder, StringComparison.OrdinalIgnoreCase)))
            other.Source = null;
        var pool = app.Pool;
        pool.Reload();
        pool.Settings.Lending.Remove(s.Folder);
        foreach (var l in pool.Settings.Lending.Values) l.Remove(s.Folder);
        pool.Settings.Save();
        Diagnostics.Note("shortcut.removed", $"{s.Folder} keptFolder={confirm.KeepFolder}");
        selected = "";
        if (app.TrySave()) app.NotifyChanged();
    }

    // ---- Keeping figures current without losing what is being typed ----

    void RefreshLive()
    {
        if (IsDisposed || Current is not { } entry) return;
        Mine();
        var before = (usageSig, sharedSig);
        BuildHeader(entry);
        FillUsage(entry);
        FillShared(entry);
        if (before != (usageSig, sharedSig)) FitWidths();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Opened on a screen at another scale than it was built for.
        if (DeviceDpi != Theme.Dpi) Rebuild(DeviceDpi);
        FitWidths();
    }
}

/// Removing a Claude: its shortcuts go, and its folder goes to the Recycle
/// Bin unless the person keeps it.
sealed class RemoveDialog : Form
{
    readonly CheckBox keep = new() { AutoSize = true };
    public bool KeepFolder => keep.Checked;

    public RemoveDialog(string name)
    {
        Text = S.F("Remove {0}?", name);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Theme.Body;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Padding = new Padding(14);
        keep.Text = S.T("Keep the profile folder (only remove it from the list)");
        keep.ForeColor = Theme.Text;
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, BackColor = Theme.Window };
        stack.Controls.Add(new TextLabel(S.T("Its shortcuts are deleted. Its profile folder, with its sign-in and chats, goes to the Recycle Bin."))
            { MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 10) });
        stack.Controls.Add(keep);
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 420, BackColor = Theme.Window, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new FlatButton(S.T("Cancel"));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var ok = new FlatButton(S.T("Remove…").TrimEnd('…', '.'), FlatButton.Kind.Danger);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        row.Controls.Add(cancel);
        row.Controls.Add(ok);
        stack.Controls.Add(row);
        Controls.Add(stack);
        HandleCreated += (_, _) => Theme.Chrome(this);
    }
}
