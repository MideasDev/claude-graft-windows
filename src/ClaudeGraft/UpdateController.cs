using ClaudeGraft.Core;
using ClaudeGraft.Core.Updates;
using ClaudeGraft.Ui;

namespace ClaudeGraft;

/// Both kinds of update, checked on their own clocks: Claude's feed when
/// Claude Graft starts and hourly, whether a Claude is running or not (a
/// check never closes one); Claude Graft's releases every six hours, installed
/// quietly unless held, and never while its window is open.
sealed class UpdateController(TrayApp app)
{
    public ClaudeUpdates.Check? Claude { get; private set; }
    public GraftRelease? Graft { get; private set; }
    public string? GraftProblem { get; private set; }
    DateTime lastGraftCheck = DateTime.MinValue;
    bool busy;

    public string ClaudeLine()
    {
        var (_, installed) = Claude == null ? ClaudeUpdates.Installed() : (Claude.Kind, Claude.Installed);
        if (installed == null) return S.T("Not installed");
        if (Claude == null) return installed;
        if (Claude.Available) return installed + " · " + S.F("{0} available", Claude.Latest!.Version);
        return Claude.Problem != null && Claude.Latest == null ? installed : installed + " · " + S.T("Up to date");
    }

    public bool ClaudeAvailable => Claude?.Available == true;

    public async Task CheckClaude()
    {
        Claude = await ClaudeUpdates.CheckAsync();
        app.OnUi(app.NotifyChanged);
    }

    public async Task CheckGraft(bool installQuietly)
    {
        lastGraftCheck = DateTime.UtcNow;
        (Graft, GraftProblem) = await SelfUpdate.CheckAsync();
        app.OnUi(app.NotifyChanged);
        if (Graft != null && installQuietly && !SelfUpdate.Held && !app.WindowOpen) await InstallGraft();
    }

    /// The hourly tick.
    public async Task Tick()
    {
        await CheckClaude();
        if (DateTime.UtcNow - lastGraftCheck > TimeSpan.FromHours(6)) await CheckGraft(installQuietly: true);
    }

    public async Task<string?> InstallGraft()
    {
        if (Graft == null || busy) return null;
        busy = true;
        try
        {
            var (exe, problem) = await SelfUpdate.InstallAsync(Graft);
            if (exe == null) return problem;
            SelfUpdate.Relaunch(exe);
            app.OnUi(app.Quit);
            return null;
        }
        finally { busy = false; }
    }

    /// Updating Claude: asked first, and only with every Claude closed. The
    /// package is Claude's own, checked against its published checksum and
    /// installed by Windows.
    public async Task UpdateClaude(IWin32Window? owner)
    {
        if (busy) return;
        await CheckClaude();
        if (Claude is not { Available: true, Latest: { } latest })
        {
            MessageBox.Show(owner, Claude?.Problem ?? S.F("Claude {0} is up to date.", Claude?.Installed ?? ""), "Claude Desktop");
            return;
        }
        if (Claude.Kind != ClaudeUpdates.Kind.Msix)
        {
            MessageBox.Show(owner, S.T("This Claude is updated by the Microsoft Store or its own installer, not from here."), "Claude Desktop");
            return;
        }
        var open = app.Entries().Where(e => Entries.Running(e, ClaudeProcesses.Snapshot())).Select(e => e.Name).ToList();
        if (open.Count > 0)
        {
            MessageBox.Show(owner, S.F("Close every Claude first, so no work in progress is lost: {0}. Claude Graft never closes one for you.", string.Join(", ", open)),
                            "Claude Desktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var size = latest.Size > 0 ? $" ({latest.Size / 1048576} MB)" : "";
        if (MessageBox.Show(owner, S.F("Download and install Claude {0}{1}? Keep every Claude closed until it finishes.", latest.Version, size),
                            "Claude Desktop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        busy = true;
        using var progress = new ProgressWindow(S.F("Updating Claude to {0}…", latest.Version));
        if (owner != null) progress.Show(owner); else progress.Show();
        try
        {
            var problem = await ClaudeUpdates.InstallAsync(latest, new Progress<int>(p => progress.Percent = p), progress.Token);
            progress.Close();
            MessageBox.Show(owner, problem ?? S.F("Claude {0} is installed.", latest.Version), "Claude Desktop",
                            MessageBoxButtons.OK, problem == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally { busy = false; }
        await CheckClaude();
    }
}

/// A download's progress, with a way to stop it.
sealed class ProgressWindow : Form
{
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = Theme.S(18), Maximum = 100 };
    readonly CancellationTokenSource cancel = new();
    public CancellationToken Token => cancel.Token;
    public int Percent { set { if (!IsDisposed) bar.Value = Math.Clamp(value, 0, 100); } }

    public ProgressWindow(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ClientSize = new Size(Theme.S(380), Theme.S(90));
        Padding = new Padding(Theme.S(16));
        var stop = new FlatButton(S.T("Cancel")) { Dock = DockStyle.Bottom };
        stop.Click += (_, _) => cancel.Cancel();
        Controls.Add(stop);
        Controls.Add(bar);
        HandleCreated += (_, _) => Theme.Chrome(this);
        FormClosed += (_, _) => cancel.Dispose();
    }
}
