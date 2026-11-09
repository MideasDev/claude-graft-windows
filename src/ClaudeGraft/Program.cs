using ClaudeGraft.Core;

namespace ClaudeGraft;

/// One executable, two jobs. With `--open <folder>` it is what a desktop or
/// Start shortcut runs: it opens that Claude, or brings forward the one
/// already on it, and exits. Otherwise it is the notification-area app.
static class Program
{
    const string MutexName = @"Local\ClaudeGraftWindows.Tray";
    internal const string ShowEventName = @"Local\ClaudeGraftWindows.Show";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Opener.ConfirmSharers = AskAboutSharers;

        if (args.Any(a => a.Equals("--relay", StringComparison.OrdinalIgnoreCase)))
        {
            Diagnostics.Who = "relay";
            var ran = new ClaudeGraft.Core.Pool.Relay(new ClaudeGraft.Core.Pool.Relay.Options()).RunAsync().GetAwaiter().GetResult();
            return ran ? 0 : 2;
        }

        var open = Array.FindIndex(args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (open >= 0)
        {
            var folder = open + 1 < args.Length ? args[open + 1] : "";
            Diagnostics.Who = "launcher:" + folder;
            var store = Store.Load();
            return Report(store.Broken
                ? new OpenResult(OpenOutcome.Failed, S.F("{0} could not be read, so it was left as it is.", Paths.StoreFile))
                : Opener.OpenFolder(store, folder));
        }
        if (args.Any(a => a.Equals("--report", StringComparison.OrdinalIgnoreCase)))
        {
            Diagnostics.Who = "report";
            var path = StateReport.Write(Store.Load());
            if (!args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return 0;
        }
        if (args.Any(a => a.Equals("--open-main", StringComparison.OrdinalIgnoreCase)))
        {
            Diagnostics.Who = "launcher:main";
            var mainStore = Store.Load();
            return Report(Opener.OpenMain(mainStore.Broken ? null : mainStore));
        }

        var snapshot = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshot >= 0 && snapshot + 1 < args.Length)
        {
            Diagnostics.Who = "snapshot";
            if (args.Any(a => a.Equals("--es", StringComparison.OrdinalIgnoreCase))) S.Spanish = true;
            if (args.Any(a => a.Equals("--en", StringComparison.OrdinalIgnoreCase))) S.Spanish = false;
            using var inspect = new TrayApp(showWindow: false, inspectOnly: true);
            inspect.DrawSnapshots(args[snapshot + 1]);
            return 0;
        }

        var sidebarRead = Array.FindIndex(args, a => a.Equals("--sidebar-read", StringComparison.OrdinalIgnoreCase));
        if (sidebarRead >= 0 && sidebarRead + 2 < args.Length)
        {
            // --sidebar-read <profile> <result file>: reads, never writes.
            string text;
            try
            {
                var found = ClaudeGraft.Core.Sidebar.SidebarSync.Read([args[sidebarRead + 1]]);
                text = System.Text.Json.JsonSerializer.Serialize(found, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception e) { text = "FAILED " + e.Message; }
            File.WriteAllText(args[sidebarRead + 2], text);
            return 0;
        }

        var selftest = Array.FindIndex(args, a => a.Equals("--selftest-web", StringComparison.OrdinalIgnoreCase));
        if (selftest >= 0 && selftest + 1 < args.Length)
        {
            var outDir = args[selftest + 1];
            Directory.CreateDirectory(outDir);
            // Everything in a throwaway home: no account, no real profile.
            Paths.HomeOverride = Path.Combine(outDir, "home");
            var log = new System.Text.StringBuilder();
            var done = false;
            var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
            form.Load += async (_, _) =>
            {
                try
                {
                    foreach (var f in await ClaudeGraft.Web.ArtifactExport.SelfTest(outDir))
                        log.AppendLine($"export {Path.GetFileName(f)} {new FileInfo(f).Length} bytes");
                    var session = new ClaudeGraft.Web.WebSession(new ClaudeGraft.Core.Web.WebAccount { Email = "selftest" });
                    try { var who = await session.Identity(); log.AppendLine("account: " + (who == null ? "none (not 200)" : "SIGNED IN?")); }
                    catch (ClaudeGraft.Web.SignedOutException) { log.AppendLine("account: signed out (401/403 from claude.ai), as expected"); }
                    finally { session.Dispose(); }
                }
                catch (Exception e) { log.AppendLine("FAILED " + e); }
                done = true;
                form.Close();
            };
            Application.Run(form);
            File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString() + (done ? "" : "unfinished"));
            return 0;
        }

        // Started from inside a packaged app (a terminal in Claude Code, say),
        // this process inherits that package, and Windows quietly keeps
        // everything it writes under AppData in the package's own copy: a
        // second list, log and profiles root nobody else sees. Measured: the
        // window then showed an empty list while the real one held a Claude.
        // Explorer starts it again outside any package.
        if (PackageIdentity.Inherited && Environment.ProcessPath is { } self)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{self}\"") { UseShellExecute = false });
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Started again while already running: show the window of the
            // one that is there rather than putting a second icon beside it.
            try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); } catch { }
            return 0;
        }
        Diagnostics.Who = "tray";
        var quiet = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(showWindow: !quiet));
        return 0;
    }

    internal static bool AskAboutSharers(IReadOnlyList<string> sharers) =>
        MessageBox.Show(Opener.ConflictMessage(sharers), Opener.ConflictTitle, MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    static int Report(OpenResult result)
    {
        if (result.Outcome == OpenOutcome.RunningInBackground || !result.Ok)
            MessageBox.Show(result.Message, "Claude Graft", MessageBoxButtons.OK,
                            result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return result.Ok ? 0 : 1;
    }
}
