using System.Diagnostics;
using ClaudeGraft.Core;
using ClaudeGraft.Core.Web;
using ClaudeGraft.Web;

namespace ClaudeGraft.Ui;

/// What the three claude.ai pages share: the accounts along the top, signed
/// in by emailed code in Claude Graft's own window; a bar of filters; one long
/// list; a line saying what happened.
abstract class WebPage : Panel
{
    protected readonly TrayApp App;
    protected WebCenter Center => App.Web;
    protected readonly ItemList List = new() { Dock = DockStyle.Fill };
    /// Wraps onto a second line in a narrow window rather than cutting the last filter off.
    protected readonly FlowLayoutPanel Bar = new() { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, BackColor = Color.Transparent };
    readonly FlowLayoutPanel accounts = new() { Dock = DockStyle.Top, WrapContents = true, BackColor = Color.Transparent, AutoSize = true };
    readonly TextLabel status = new("", Theme.Small, Theme.SubText) { Dock = DockStyle.Bottom, AutoSize = false };
    protected readonly ToolTip Tips = new() { InitialDelay = 300 };
    protected bool Loaded;
    bool busy;

    protected static int S(float px) => Theme.S(px);
    protected static string T(string s) => Core.S.T(s);

    protected WebPage(TrayApp app)
    {
        App = app;
        BackColor = Theme.Window;
        Padding = new Padding(S(20), S(4), S(20), S(8));
        status.Height = S(24);
        Bar.Padding = new Padding(0, S(6), 0, S(4));
        accounts.Padding = new Padding(0, 0, 0, S(4));
        List.Open += row => OpenRow(row);
        List.Menu += (row, at) => RowMenu(row)?.Show(List, at);
        Controls.Add(List);
        Controls.Add(Bar);
        Controls.Add(accounts);
        Controls.Add(status);
        Center.Changed += OnCenterChanged;
        Disposed += (_, _) => Center.Changed -= OnCenterChanged;
        BuildAccounts();
    }

    void OnCenterChanged() { if (!IsDisposed) BuildAccounts(); }

    /// Called whenever the page is shown: lists are asked for then, never on a timer.
    public virtual void Shown() { if (!Loaded && Center.Accounts.Count > 0) _ = Reload(); }

    protected abstract Task Load();
    protected abstract void OpenRow(ListRow row);
    protected abstract ContextMenuStrip? RowMenu(ListRow row);
    protected abstract void Redraw();

    public async Task Reload()
    {
        if (busy) return;
        busy = true;
        Say(T("Asking claude.ai…"));
        try { await Load(); Loaded = true; }
        catch (Exception e) { Say(e.Message); }
        finally { busy = false; }
    }

    protected void Say(string text) { if (!status.IsDisposed) status.Text = text; }

    protected void Problems(List<string> problems, string done) => Say(problems.Count > 0 ? string.Join("  ·  ", problems) : done);

    protected Color ColorOf(Guid account)
    {
        var i = Center.Accounts.FindIndex(a => a.Id == account);
        return IconMaker.Parse(Store.Palette[(Math.Max(0, i) + 1) % Store.Palette.Length]);
    }

    protected string NameOf(Guid account) => Center.Accounts.FirstOrDefault(a => a.Id == account)?.Name ?? "—";

    protected static string When(DateTime? d) => d is { } t ? t.ToLocalTime().ToString("d MMM yyyy") : "";

    protected static void Browse(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    protected static void CopyLink(string? url) { if (!string.IsNullOrEmpty(url)) try { Clipboard.SetText(url); } catch { } }

    /// Every way into a sign-in says the one way supported.
    void BuildAccounts()
    {
        accounts.SuspendLayout();
        foreach (Control c in accounts.Controls.Cast<Control>().ToList()) c.Dispose();
        accounts.Controls.Clear();
        foreach (var a in Center.Accounts)
        {
            var lapsed = Center.SignedOut.Contains(a.Id);
            var chip = new FlatButton(lapsed ? "⚠ " + a.Name : a.Name, FlatButton.Kind.Secondary) { Chevron = true, Margin = new Padding(0, S(4), S(6), S(4)) };
            chip.AutoSizeNow();
            Tips.SetToolTip(chip, lapsed ? T("This sign-in has lapsed. Sign in again.") : a.Org ?? "");
            chip.Click += (_, _) =>
            {
                var menu = ThemedRenderer.Menu();
                if (lapsed) menu.Items.Add(T("Sign in again…"), null, (_, _) => SignIn());
                menu.Items.Add(T("Sign out"), null, (_, _) =>
                {
                    if (MessageBox.Show(this, Core.S.F("Sign {0} out of Claude Graft? Its sign-in here is removed; claude.ai is not changed.", a.Name),
                                        T("Sign out"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    Center.SignOut(a);
                    _ = Reload();
                });
                menu.Show(chip, new Point(0, chip.Height));
            };
            accounts.Controls.Add(chip);
        }
        var add = new FlatButton(Center.Accounts.Count == 0 ? T("Sign in to claude.ai…") : T("Add an account…"),
                                 Center.Accounts.Count == 0 ? FlatButton.Kind.Primary : FlatButton.Kind.Quiet, Theme.GAdd)
                  { Margin = new Padding(0, S(4), S(6), S(4)) };
        add.AutoSizeNow();
        add.Click += (_, _) => SignIn();
        accounts.Controls.Add(add);
        var refresh = new FlatButton("", FlatButton.Kind.Quiet, Theme.GRefresh) { Margin = new Padding(0, S(4), 0, S(4)) };
        Tips.SetToolTip(refresh, T("Refresh"));
        refresh.Click += (_, _) => _ = Reload();
        accounts.Controls.Add(refresh);
        accounts.Controls.Add(new TextLabel(T("Choose “Continue with email” and type the code claude.ai sends you."), Theme.Small, Theme.Faint)
            { Margin = new Padding(S(4), S(12), 0, 0) });
        accounts.ResumeLayout();
    }

    void SignIn()
    {
        var account = Center.SignIn(FindForm()!);
        if (account != null) _ = Reload();
    }
}

// ======================================================================
// Artifacts
// ======================================================================

sealed class ArtifactsPage : WebPage
{
    List<Artifact> all = [];
    readonly Field filter = new("");
    string side = "all";
    Guid? onlyAccount;
    readonly FlatButton sidePicker, accountPicker;

    public ArtifactsPage(TrayApp app) : base(app)
    {
        filter.Width = S(260);
        filter.Margin = new Padding(0, 0, S(8), 0);
        filter.Box.PlaceholderText = T("Filter by title");
        filter.Box.TextChanged += (_, _) => Redraw();
        sidePicker = new FlatButton(T("All"), FlatButton.Kind.Quiet) { Chevron = true };
        sidePicker.Click += (_, _) =>
        {
            var menu = ThemedRenderer.Menu();
            void Option(string label, string value) => menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) =>
                { side = value; sidePicker.Text = label; Redraw(); }) { Checked = side == value });
            menu.ShowCheckMargin = true;
            Option(T("All"), "all");
            Option(T("Mine"), "mine");
            Option(T("Shared with me"), "shared");
            menu.Show(sidePicker, new Point(0, sidePicker.Height));
        };
        accountPicker = new FlatButton(T("Every account"), FlatButton.Kind.Quiet) { Chevron = true };
        accountPicker.Click += (_, _) => AccountMenu(accountPicker, id => { onlyAccount = id; Redraw(); }).Show(accountPicker, new Point(0, accountPicker.Height));
        var groups = new FlatButton(T("Groups…"), FlatButton.Kind.Quiet, Theme.GPeople);
        groups.Click += (_, _) => { using var d = new GroupsDialog(Center); d.ShowDialog(FindForm()); };
        foreach (var c in new Control[] { filter, sidePicker, accountPicker, groups }) Bar.Controls.Add(c);
        List.Empty = T("Sign in to an account to see its artifacts.");
    }

    ContextMenuStrip AccountMenu(FlatButton picker, Action<Guid?> chosen)
    {
        var menu = ThemedRenderer.Menu();
        menu.Items.Add(T("Every account"), null, (_, _) => { picker.Text = T("Every account"); chosen(null); });
        foreach (var a in Center.Accounts) menu.Items.Add(a.Name, null, (_, _) => { picker.Text = a.Name; chosen(a.Id); });
        return menu;
    }

    protected override async Task Load()
    {
        var (artifacts, problems) = await Center.ListArtifacts();
        all = artifacts;
        Redraw();
        Problems(problems, Core.S.F("{0} artifacts.", all.Count));
    }

    protected override void Redraw()
    {
        var words = filter.Text.Trim();
        var shown = all.Where(a => (side == "all" || (side == "mine") == a.IsOwn) && (onlyAccount == null || a.Account == onlyAccount) &&
                                   (words.Length == 0 || a.Title.Contains(words, StringComparison.CurrentCultureIgnoreCase)));
        List.Empty = Center.Accounts.Count == 0 ? T("Sign in to an account to see its artifacts.") : Loaded ? T("No artifacts here.") : "";
        List.SetRows([.. shown.Select(a => new ListRow(a, a.Title,
            $"{NameOf(a.Account)} · {a.AccessLabel}" + (a.IsOwn ? "" : " · " + T("shared with you")),
            When(a.SortTime), ColorOf(a.Account), IconMaker.Mark(NameOf(a.Account))))]);
    }

    protected override void OpenRow(ListRow row) => Browse(((Artifact)row.Tag).OpenUrl);

    protected override ContextMenuStrip? RowMenu(ListRow row)
    {
        var a = (Artifact)row.Tag;
        var menu = ThemedRenderer.Menu();
        menu.ShowCheckMargin = true;
        menu.Items.Add(new ToolStripMenuItem(T("Open"), null, (_, _) => Browse(a.OpenUrl)) { Enabled = a.OpenUrl != null });
        menu.Items.Add(new ToolStripMenuItem(T("Copy link"), null, (_, _) => { CopyLink(a.OpenUrl); Say(T("Link copied.")); }) { Enabled = a.OpenUrl != null });
        menu.Items.Add(new ToolStripSeparator());

        if (a.IsOwn)
        {
            var share = new ToolStripMenuItem(T("Share"));
            share.DropDown = ThemedRenderer.Menu();
            ((ToolStripDropDownMenu)share.DropDown).ShowCheckMargin = true;
            share.DropDownItems.Add(new ToolStripMenuItem(T("Anyone with the link"), null, async (_, _) =>
            {
                Say(T("Asking claude.ai…"));
                var problem = await Center.SetPublic(a, !a.IsPublic);
                if (problem != null) Say(problem); else await Reload();
            }) { Checked = a.IsPublic });
            share.DropDownItems.Add(T("Invite people…"), null, async (_, _) =>
            {
                using var d = new InviteDialog(a.Title);
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                await Invite(a, d.People);
            });
            if (Center.Groups.Count > 0)
            {
                share.DropDownItems.Add(new ToolStripSeparator());
                foreach (var g in Center.Groups)
                    share.DropDownItems.Add(Core.S.F("Group: {0}", g.Name), null, async (_, _) =>
                        await Invite(a, g.Emails.Select(e => (e, g.RoleOf(e))).ToList()));
            }
            menu.Items.Add(share);
        }
        if (a.IsOwn && a.Renamable)
            menu.Items.Add(T("Rename…"), null, async (_, _) =>
            {
                using var d = new InputDialog(T("Rename"), a.Title);
                if (d.ShowDialog(FindForm()) != DialogResult.OK || d.Value.Trim().Length == 0) return;
                var problem = await Center.Rename(a, d.Value.Trim());
                if (problem != null) Say(problem); else await Reload();
            });
        if (a.Duplicable)
            menu.Items.Add(new ToolStripMenuItem(T("Duplicate"), null, async (_, _) =>
            {
                Say(T("Asking claude.ai…"));
                var problem = await Center.Duplicate(a);
                if (problem != null) Say(problem); else await Reload();
            }) { Enabled = a.DuplicateAvailable, ToolTipText = a.DuplicateAvailable ? "" : T("claude.ai allows no more copies today.") });

        var export = new ToolStripMenuItem(T("Export"));
        export.DropDown = ThemedRenderer.Menu();
        foreach (var f in Enum.GetValues<ArtifactExport.Format>())
            export.DropDownItems.Add(ArtifactExport.Label(f), null, async (_, _) =>
            {
                Say(T("Exporting…"));
                var problem = await ArtifactExport.Run(Center, a, f, FindForm()!);
                Say(problem ?? T("Exported."));
            });
        menu.Items.Add(export);

        if (a.IsOwn)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(T("Delete…"), null, async (_, _) =>
            {
                if (MessageBox.Show(FindForm(), Core.S.F("Delete “{0}”? claude.ai keeps no copy of a deleted artifact.", a.Title),
                                    T("Delete"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                var problem = await Center.Delete(a);
                if (problem != null) Say(problem); else await Reload();
            });
        }
        return menu;
    }

    async Task Invite(Artifact a, IReadOnlyList<(string Email, ArtifactRole Role)> people)
    {
        if (people.Count == 0) return;
        Say(T("Asking claude.ai…"));
        var r = await Center.Invite(a, people);
        var text = Core.S.F("{0} of {1} invited.", r.Sent, people.Count);
        if (r.Lowered > 0) text += " " + Core.S.F("{0} with less than the group asked, as the artifact allows.", r.Lowered);
        if (r.Failed.Count > 0) text += " " + string.Join("; ", r.Failed);
        Say(text);
        await Reload();
        Say(text);
    }
}

// ======================================================================
// Projects
// ======================================================================

sealed class ProjectsPage : WebPage
{
    List<ClaudeProject> all = [];
    readonly Field filter = new("");

    public ProjectsPage(TrayApp app) : base(app)
    {
        filter.Width = S(260);
        filter.Box.PlaceholderText = T("Filter by name");
        filter.Box.TextChanged += (_, _) => Redraw();
        Bar.Controls.Add(filter);
        List.Empty = T("Sign in to an account to see its projects.");
    }

    protected override async Task Load()
    {
        var (projects, problems) = await Center.ListProjects();
        all = projects;
        Redraw();
        Problems(problems, Core.S.F("{0} projects.", all.Count));
    }

    protected override void Redraw()
    {
        var words = filter.Text.Trim();
        List.Empty = Center.Accounts.Count == 0 ? T("Sign in to an account to see its projects.") : Loaded ? T("No projects here.") : "";
        List.SetRows([.. all.Where(p => words.Length == 0 || p.Name.Contains(words, StringComparison.CurrentCultureIgnoreCase) ||
                                         p.Summary.Contains(words, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new ListRow(p, (p.IsStarred ? "★ " : "") + p.Name,
                NameOf(p.Account) + (p.Summary.Length > 0 ? " · " + p.Summary.ReplaceLineEndings(" ") : ""),
                (p.IsPrivate ? T("Private") : T("Shared")) + "  " + When(p.Updated), ColorOf(p.Account), IconMaker.Mark(NameOf(p.Account))))]);
    }

    protected override void OpenRow(ListRow row) => Browse(((ClaudeProject)row.Tag).OpenUrl);

    protected override ContextMenuStrip? RowMenu(ListRow row)
    {
        var p = (ClaudeProject)row.Tag;
        var menu = ThemedRenderer.Menu();
        menu.Items.Add(T("Open"), null, (_, _) => Browse(p.OpenUrl));
        menu.Items.Add(T("Copy link"), null, (_, _) => { CopyLink(p.OpenUrl); Say(T("Link copied.")); });
        return menu;
    }
}

// ======================================================================
// Search Chats
// ======================================================================

sealed class SearchPage : WebPage
{
    List<SearchHit> hits = [];
    readonly Field query = new("");
    string kind = "all", state = "active";
    Guid? onlyAccount;

    public SearchPage(TrayApp app) : base(app)
    {
        query.Width = S(220);
        query.Margin = new Padding(0, 0, S(6), 0);
        query.Box.PlaceholderText = T("Search every account's chats");
        // Asked on Return or the button, never as the query is typed.
        query.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Reload(); } };
        var go = new FlatButton(T("Search"), FlatButton.Kind.Primary);
        go.Click += (_, _) => _ = Reload();
        var kindPicker = Picker(T("Chats and Code"), [(T("Chats and Code"), "all"), (T("Chats"), "chat"), (T("Code"), "code")], v => kind = v);
        var statePicker = Picker(T("Active"), [(T("Active"), "active"), (T("Archived"), "archived"), (T("Active and archived"), "both")], v => state = v);
        var accountPicker = new FlatButton(T("Every account"), FlatButton.Kind.Quiet) { Chevron = true };
        accountPicker.Click += (_, _) =>
        {
            var menu = ThemedRenderer.Menu();
            menu.Items.Add(T("Every account"), null, (_, _) => { accountPicker.Text = T("Every account"); onlyAccount = null; Redraw(); });
            foreach (var a in Center.Accounts) menu.Items.Add(a.Name, null, (_, _) => { accountPicker.Text = a.Name; onlyAccount = a.Id; Redraw(); });
            menu.Show(accountPicker, new Point(0, accountPicker.Height));
        };
        foreach (var c in new Control[] { query, go, kindPicker, statePicker, accountPicker }) Bar.Controls.Add(c);
        List.Empty = T("Type words and press Return to search every account's chats and Code sessions.");
    }

    /// Changing a filter asks nothing: it narrows what came back.
    FlatButton Picker(string first, (string Label, string Value)[] options, Action<string> chosen)
    {
        var picker = new FlatButton(first, FlatButton.Kind.Quiet) { Chevron = true };
        picker.Click += (_, _) =>
        {
            var menu = ThemedRenderer.Menu();
            foreach (var (label, value) in options) menu.Items.Add(label, null, (_, _) => { picker.Text = label; chosen(value); Redraw(); });
            menu.Show(picker, new Point(0, picker.Height));
        };
        return picker;
    }

    public override void Shown() { }

    protected override async Task Load()
    {
        var words = query.Text.Trim();
        if (words.Length == 0) { Say(""); return; }
        var (found, problems) = await Center.Search(words);
        hits = found;
        Redraw();
        Problems(problems, Core.S.F("{0} found.", hits.Count));
    }

    protected override void Redraw()
    {
        var shown = hits.Where(h =>
                (kind == "all" || (kind == "chat") == (h.Kind == HitKind.Chat)) &&
                (state == "both" || (state == "archived") == h.IsArchived) &&
                (onlyAccount == null || h.Account == onlyAccount))
            .OrderBy(h => h.Kind).ThenBy(h => h.Rank);
        if (Loaded) List.Empty = T("Nothing found with these filters.");
        List.SetRows([.. shown.Select(h => new ListRow(h, h.Title,
            NameOf(h.Account) + (h.Snippet.Length > 0 ? " · " + h.Snippet.ReplaceLineEndings(" ") : ""),
            (h.Kind == HitKind.Chat ? T("Chat") : h.InCloud ? T("Code · cloud") : T("Code")) + "  " + When(h.Updated),
            ColorOf(h.Account), IconMaker.Mark(NameOf(h.Account)), Dim: h.IsArchived, Badge: h.IsArchived ? T("archived") : ""))]);
    }

    protected override void OpenRow(ListRow row) => Browse(((SearchHit)row.Tag).OpenUrl);

    protected override ContextMenuStrip? RowMenu(ListRow row)
    {
        var h = (SearchHit)row.Tag;
        var menu = ThemedRenderer.Menu();
        menu.Items.Add(T("Open"), null, (_, _) => Browse(h.OpenUrl));
        menu.Items.Add(T("Copy link"), null, (_, _) => { CopyLink(h.OpenUrl); Say(T("Link copied.")); });
        return menu;
    }
}

// ======================================================================
// Dialogs
// ======================================================================

/// The base of the small dialogs: themed, centred on their owner.
class ThemedDialog : Form
{
    protected readonly FlowLayoutPanel Stack = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
    protected readonly FlowLayoutPanel Buttons = new() { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
    protected static int S(float px) => Theme.S(px);

    protected ThemedDialog(string title, int width)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Padding = new Padding(S(16));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Stack.AutoSize = true;
        Buttons.Width = S(width);
        Buttons.Margin = new Padding(0, S(12), 0, 0);
        Controls.Add(Stack);
        HandleCreated += (_, _) => Theme.Chrome(this);
    }

    protected FlatButton Ok(string label)
    {
        var cancel = new FlatButton(Core.S.T("Cancel"));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var ok = new FlatButton(label, FlatButton.Kind.Primary);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Buttons.Controls.Add(cancel);
        Buttons.Controls.Add(ok);
        Stack.Controls.Add(Buttons);
        return ok;
    }

    protected static TextBox Box(int width, int lines = 1) => new()
    {
        Width = S(width), Height = lines == 1 ? S(28) : S(lines * 20), Multiline = lines > 1, ScrollBars = lines > 1 ? ScrollBars.Vertical : ScrollBars.None,
        BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body,
    };
}

sealed class InputDialog : ThemedDialog
{
    readonly TextBox box;
    public string Value => box.Text;

    public InputDialog(string title, string value) : base(title, 360)
    {
        box = Box(360);
        box.Text = value;
        Stack.Controls.Add(box);
        Ok(Core.S.T("OK"));
        AcceptButton = null;
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); } };
    }
}

sealed class InviteDialog : ThemedDialog
{
    readonly TextBox emails = Box(400, 5);
    ArtifactRole role = ArtifactRole.Reader;

    public List<(string Email, ArtifactRole Role)> People =>
        ArtifactGroup.ParseAddresses(emails.Text).Select(e => (e, role)).ToList();

    public InviteDialog(string title) : base(Core.S.F("Invite people to “{0}”", title), 400)
    {
        Stack.Controls.Add(new TextLabel(Core.S.T("Addresses, one per line or pasted from an email. claude.ai emails each of them."), Theme.Small, Theme.SubText)
            { MaximumSize = new Size(S(400), 0), Margin = new Padding(0, 0, 0, S(6)) });
        Stack.Controls.Add(emails);
        var picker = new FlatButton(Roles.Label(role), FlatButton.Kind.Quiet) { Chevron = true, Margin = new Padding(0, S(8), 0, 0) };
        picker.Click += (_, _) =>
        {
            var menu = ThemedRenderer.Menu();
            foreach (var r in Enum.GetValues<ArtifactRole>()) menu.Items.Add(Roles.Label(r), null, (_, _) => { role = r; picker.Text = Roles.Label(r); });
            menu.Show(picker, new Point(0, picker.Height));
        };
        Stack.Controls.Add(picker);
        Ok(Core.S.T("Invite"));
    }
}

/// Claude Graft's own groups: a name, the addresses, and what each may do.
sealed class GroupsDialog : ThemedDialog
{
    readonly WebCenter center;
    readonly ListBox groups = new() { Width = S(180), Height = S(240), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox name = Box(300);
    readonly TextBox emails = Box(300, 8);
    readonly FlatButton rolePicker = new(Roles.Label(ArtifactRole.Reader), FlatButton.Kind.Quiet) { Chevron = true };
    ArtifactGroup? current;
    ArtifactRole? pickedRole;

    public GroupsDialog(WebCenter center) : base(Core.S.T("Groups"), 500)
    {
        this.center = center;
        groups.Font = Theme.Body;
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent };
        left.Controls.Add(groups);
        var add = new FlatButton(Core.S.T("New group"), FlatButton.Kind.Quiet, Theme.GAdd);
        add.Click += (_, _) =>
        {
            Keep();
            var g = new ArtifactGroup { Name = Core.S.T("New group") };
            center.Groups.Add(g);
            Fill(g);
        };
        var remove = new FlatButton(Core.S.T("Delete group"), FlatButton.Kind.Quiet, Theme.GRemove);
        remove.Click += (_, _) =>
        {
            if (current == null) return;
            center.Groups.Remove(current);
            current = null;
            Fill(center.Groups.FirstOrDefault());
        };
        left.Controls.Add(add);
        left.Controls.Add(remove);
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(S(12), 0, 0, 0) };
        right.Controls.Add(new TextLabel(Core.S.T("Name"), Theme.Small, Theme.SubText));
        right.Controls.Add(name);
        right.Controls.Add(new TextLabel(Core.S.T("Addresses, one per line"), Theme.Small, Theme.SubText) { Margin = new Padding(0, S(8), 0, 0) });
        right.Controls.Add(emails);
        rolePicker.Click += (_, _) =>
        {
            var menu = ThemedRenderer.Menu();
            foreach (var r in Enum.GetValues<ArtifactRole>())
                menu.Items.Add(Roles.Label(r), null, (_, _) => { pickedRole = r; rolePicker.Text = Roles.Label(r); });
            menu.Show(rolePicker, new Point(0, rolePicker.Height));
        };
        right.Controls.Add(rolePicker);
        row.Controls.Add(left);
        row.Controls.Add(right);
        Stack.Controls.Add(row);
        groups.SelectedIndexChanged += (_, _) => { if (groups.SelectedItem is ArtifactGroupItem item && item.Group != current) { Keep(); Fill(item.Group); } };
        var ok = Ok(Core.S.T("Save"));
        ok.Click += (_, _) => { Keep(); center.SaveGroups(); };
        Fill(center.Groups.FirstOrDefault());
    }

    sealed record ArtifactGroupItem(ArtifactGroup Group) { public override string ToString() => Group.Name; }

    void Fill(ArtifactGroup? g)
    {
        current = g;
        pickedRole = null;
        groups.Items.Clear();
        foreach (var x in center.Groups) groups.Items.Add(new ArtifactGroupItem(x));
        if (g != null) groups.SelectedIndex = center.Groups.IndexOf(g);
        name.Enabled = emails.Enabled = rolePicker.Enabled = g != null;
        name.Text = g?.Name ?? "";
        emails.Text = g == null ? "" : string.Join(Environment.NewLine, g.Emails);
        rolePicker.Text = g?.CommonRole is { } r ? Roles.Label(r) : Core.S.T("Mixed roles");
    }

    /// What is on screen written back to the group: new addresses join with
    /// the role everyone already has, and a role picked here goes to all.
    void Keep()
    {
        if (current == null) return;
        current.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : current.Name;
        var wanted = ArtifactGroup.ParseAddresses(emails.Text);
        current.Emails.RemoveAll(e => !wanted.Contains(e));
        if (current.Roles != null)
            foreach (var gone in current.Roles.Keys.Where(k => !wanted.Contains(k)).ToList()) current.Roles.Remove(gone);
        current.Add(wanted);
        if (pickedRole is { } r) current.SetRoleForEveryone(r);
    }
}
