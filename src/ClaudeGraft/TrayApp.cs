using System.Diagnostics;
using ClaudeGraft.Core;
using ClaudeGraft.Ui;

namespace ClaudeGraft;

/// The notification-area app. Its icon says which Claude is in front, in
/// that Claude's colour with its account's usage on it, and its menu lists
/// every Claude with its usage and opens or brings forward any of them.
sealed class TrayApp : ApplicationContext
{
    public Store Store { get; private set; }
    public UsageMonitor Monitor { get; } = new();
    public PoolController Pool { get; }
    public UpdateController Updates { get; }
    readonly System.Windows.Forms.Timer hourly = new() { Interval = 60 * 60 * 1000 };
    public bool WindowOpen => window is { IsDisposed: false, Visible: true };
    /// claude.ai accounts for Artifacts, Projects and Search Chats.
    public Web.WebCenter Web { get; } = new();
    public IReadOnlyList<ClaudeProcess> Snapshot { get; private set; } = [];
    public event Action? Changed;

    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = ThemedRenderer.Menu();
    TrayPopup? popup;
    SettingsWindow? settings;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer front = new() { Interval = 1_500 };
    readonly Control invoker = new();
    readonly EventWaitHandle? showSignal;
    MainWindow? window;
    string frontKey = "";
    DateTime lastScan = DateTime.MinValue;
    Icon? currentIcon;

    /// `inspectOnly` builds the state the windows read and nothing else: no
    /// icon, no timers, no mirror pass, no relay, no shortcut rewritten. It
    /// is what `--snapshot` draws the windows from.
    public TrayApp(bool showWindow, bool inspectOnly = false)
    {
        _ = invoker.Handle;
        Pool = new PoolController(this);
        Updates = new UpdateController(this);
        Store = Store.Load();
        if (inspectOnly) { Scan(); return; }
        if (Store.Broken)
            MessageBox.Show(S.F("{0} could not be read, so it was left as it is.", Paths.StoreFile),
                            S.T("Claude Graft could not save its list"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
        {
            if (ShellLinks.Refresh(Store)) TrySave();
            Startup.Refresh();
        }

        Opener.Background = work => Task.Run(() => { try { work(); } catch (Exception e) { Diagnostics.Note("background.failed", e.Message); } });
        menu.Opening += (_, _) => BuildMenu();
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        tray.Visible = true;

        tick.Tick += (_, _) => { Scan(); _ = RefreshUsage(Strength.Tick); Pool.Tick(); };
        front.Tick += (_, _) => WatchFront();
        tick.Start();
        front.Start();

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => OnUi(ShowWindow), null, -1, false);

        Scan();
        UpdateIcon();
        _ = RefreshUsage(Strength.Look);
        SquareUp();
        Pool.Tick();
        // Updates: Claude's feed now and hourly; Claude Graft's own a minute
        // after starting, so a release does not race a window being opened.
        Core.Updates.SelfUpdate.SweepPrevious();
        _ = Updates.CheckClaude();
        hourly.Tick += (_, _) => _ = Updates.Tick();
        hourly.Start();
        var first = new System.Windows.Forms.Timer { Interval = 60_000 };
        first.Tick += (_, _) => { first.Stop(); first.Dispose(); _ = Updates.CheckGraft(installQuietly: true); };
        first.Start();
        if (showWindow) ShowWindow();
    }

    public void OnUi(Action work)
    {
        if (invoker.IsDisposed) return;
        if (invoker.InvokeRequired) invoker.BeginInvoke(work); else work();
    }

    public List<ClaudeEntry> Entries() => ClaudeGraft.Entries.All(Store);

    /// The mirror passes run on no timer: at Claude Graft's start, when the
    /// menu opens, and whenever a Claude is opened, as on macOS. A pass is
    /// cheap when nothing has changed.
    public void SquareUp()
    {
        if (Store.Data.Shortcuts.All(s => string.IsNullOrEmpty(s.Source)) && Graft.PairKeys().Count == 0) return;
        Opener.Background(() => Graft.MirrorKnownPairs());
    }

    public void Scan()
    {
        Snapshot = ClaudeProcesses.Snapshot();
        lastScan = DateTime.UtcNow;
    }

    public bool TrySave()
    {
        try { Store.Save(); return true; }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, S.T("Claude Graft could not save its list"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public void NotifyChanged()
    {
        UpdateIcon();
        Changed?.Invoke();
    }

    public async Task RefreshUsage(Strength strength, string? only = null)
    {
        if (Store.Data.LiveUsage)
        {
            var entries = Entries().Where(e => e.ProfileDir != null && (only == null || e.Key == only)).ToList();
            var snapshot = Snapshot;
            var running = entries.ToDictionary(e => e.ProfileDir!, e => ClaudeGraft.Entries.Running(e, snapshot),
                                               StringComparer.OrdinalIgnoreCase);
            await Task.Run(() => Monitor.RefreshAsync(running.Keys, strength,
                p => running.TryGetValue(p, out var r) && r));
        }
        OnUi(() =>
        {
            NotifyChanged();
        });
    }

    // ---- The icon ----

    /// Bringing a Claude forward asks for its account at the strength opening
    /// the menu does, since it is the figure about to be looked at.
    void WatchFront()
    {
        var pid = WindowTools.ForegroundPid();
        if (pid == null) return;
        var key = ClaudeProcesses.ProfileOf(pid.Value, Snapshot);
        if (key == null && DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(5) && IsClaudePid(pid.Value))
        {
            Scan();
            key = ClaudeProcesses.ProfileOf(pid.Value, Snapshot);
        }
        if (key == null || key == frontKey) return;
        frontKey = key;
        UpdateIcon();
        _ = RefreshUsage(Strength.Look, key);
    }

    static bool IsClaudePid(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    void UpdateIcon()
    {
        var entries = Entries();
        var entry = entries.FirstOrDefault(e => e.Key == frontKey) ?? entries[0];
        var now = DateTime.UtcNow;
        var figure = Monitor.Entry(entry.ProfileDir);
        var (text, weekly) = figure == null ? (IconMaker.Mark(entry.Name), false)
                                            : (figure.Headline.Percent.ToString(), figure.Headline.Weekly);
        var icon = IconMaker.TrayIcon(IconMaker.Parse(entry.Color), text, weekly);
        tray.Icon = icon;
        currentIcon?.Dispose();
        currentIcon = icon;

        var tip = $"{entry.Name} — {ClaudeGraft.Entries.Short(figure, now)}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // ---- The dropdown and the menu ----

    /// A left click opens the dropdown, which asks for current figures the
    /// way opening the macOS dropdown does.
    void ShowPopup()
    {
        popup ??= new TrayPopup(this);
        Scan();
        _ = RefreshUsage(Strength.Look);
        SquareUp();
        Pool.Tick();
        popup.Toggle(Cursor.Position);
    }

    /// A right click gets the short menu Windows people reach for there.
    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(S.T("Open Claude Graft"), null, (_, _) => ShowWindow());
        menu.Items.Add(S.T("New Claude…"), null, (_, _) => { ShowWindow(); window?.NewClaude(); });
        menu.Items.Add(S.T("Settings…"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(S.T("Quit Claude Graft"), null, (_, _) => Quit());
    }

    /// Draws the window on each Claude, the dropdown and settings into PNG
    /// files, for checking the layout without a person at the screen.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    public void DrawSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        void Save(Control c, string name)
        {
            // PrintWindow, not DrawToBitmap: it paints the way the screen
            // does, at the screen's scale, which is where a mismatch shows.
            using var bitmap = new Bitmap(c.Width, c.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                var dc = g.GetHdc();
                try { PrintWindow(c.Handle, dc, 2); } finally { g.ReleaseHdc(dc); }
            }
            bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        // Off screen, beside each monitor in turn: Windows gives a window the
        // scale of the screen nearest to it, so each place draws at one
        // screen's scale, and moving between them is a change of screen.
        var places = Screen.AllScreens.Select(s => new Point(s.Bounds.Right - 2, s.Bounds.Top + 20)).ToList();
        var main = new MainWindow(this) { StartPosition = FormStartPosition.Manual, Location = places[0] };
        main.Show();
        Application.DoEvents();
        Save(main, "window-main");
        for (var i = 1; i < places.Count; i++)
        {
            main.Location = places[i];
            Application.DoEvents();
            Save(main, $"window-moved-{i}-dpi{main.DeviceDpi}");
        }
        main.Location = places[0];
        Application.DoEvents();
        Save(main, $"window-back-dpi{main.DeviceDpi}");
        var index = 0;
        foreach (var entry in Entries().Skip(1))
        {
            main.SelectForSnapshot(entry.Key);
            Application.DoEvents();
            Save(main, "window-" + (++index));
        }
        main.NewClaude();
        Application.DoEvents();
        Save(main, "window-new");
        foreach (var page in new[] { "#artifacts", "#projects", "#search" })
        {
            main.SelectForSnapshot(page);
            Application.DoEvents();
            Save(main, "page-" + page[1..]);
        }
        main.Close();
        var drop = new TrayPopup(this);
        drop.ShowForSnapshot(new Point(-4000 + 400, 900));
        Application.DoEvents();
        Save(drop, "dropdown");
        drop.Close();
        var set = new SettingsWindow(this) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) };
        set.Show();
        Application.DoEvents();
        Save(set, "settings");
        set.Close();
    }

    public void ShowSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsWindow(this);
            settings.FormClosed += (_, _) => settings = null;
        }
        settings.Show();
        settings.Activate();
    }

    static bool SafeStartupEnabled() { try { return Startup.Enabled; } catch { return false; } }

    public void OpenEntry(ClaudeEntry entry)
    {
        var result = ClaudeGraft.Entries.Open(entry, Store);
        if (result.Outcome == OpenOutcome.Launched)
            tray.ShowBalloonTip(2000, "Claude Graft", result.Message, ToolTipIcon.None);
        else if (result.Outcome != OpenOutcome.Revealed)
            MessageBox.Show(result.Message, "Claude Graft", MessageBoxButtons.OK,
                            result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        Scan();
        NotifyChanged();
    }

    public void ToggleLiveUsage()
    {
        if (!Store.Data.LiveUsage)
        {
            var answer = MessageBox.Show(
                S.T("Live usage decrypts each profile's access token on this PC (with your Windows account's own protection) and sends it to api.anthropic.com only. Turn it on?"),
                S.T("Read live usage from Anthropic"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        Store.Data.LiveUsage = !Store.Data.LiveUsage;
        if (TrySave()) _ = RefreshUsage(Strength.Press);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    void About() => MessageBox.Show(
        $"Claude Graft for Windows {Application.ProductVersion.Split('+')[0]}\n\n" +
        S.T("Claude Graft runs every Claude account you have side by side, each signed in on its own profile.") +
        "\n\nMIT — github.com/aaditya-v-more/claude-graft",
        S.T("About Claude Graft"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void ShowWindow()
    {
        if (window == null || window.IsDisposed)
        {
            window = new MainWindow(this);
            window.FormClosed += (_, _) => window = null;
        }
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    public void Quit()
    {
        popup?.Close();
        settings?.Close();
        tick.Stop();
        front.Stop();
        tray.Visible = false;
        window?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            menu.Dispose();
            tick.Dispose();
            hourly.Dispose();
            front.Dispose();
            showSignal?.Dispose();
            invoker.Dispose();
            currentIcon?.Dispose();
            Web.Dispose();
        }
        base.Dispose(disposing);
    }
}
