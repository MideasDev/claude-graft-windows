using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using ClaudeGraft.Core.Web;
using Microsoft.Web.WebView2.Core;

namespace ClaudeGraft.Web;

/// Runs requests from inside a claude.ai page, so the sign-in cookie goes
/// only where the browser engine sends it and Claude Graft never holds it.
/// A page cannot hand back a promise to the host, so each request posts its
/// answer back by id.
sealed class PageFetch
{
    readonly CoreWebView2 view;
    readonly Dictionary<string, TaskCompletionSource<(int, string)>> pending = [];

    public PageFetch(CoreWebView2 view)
    {
        this.view = view;
        view.WebMessageReceived += (_, e) =>
        {
            try
            {
                var o = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
                var id = o?["id"]?.GetValue<string>();
                if (id == null || !pending.Remove(id, out var done)) return;
                done.TrySetResult((o!["status"]?.GetValue<int>() ?? 0, o["body"]?.GetValue<string>() ?? ""));
            }
            catch { }
        };
    }

    const string Same = """
        (async () => { const a = ARGS;
          try {
            const r = await fetch(a.path, {method: a.method, credentials: "same-origin", headers: a.headers,
                                           body: a.body === null ? undefined : a.body});
            chrome.webview.postMessage({id: a.id, status: r.status, body: await r.text()});
          } catch (e) { chrome.webview.postMessage({id: a.id, status: 0, body: String(e)}); }
        })();
        """;

    // A file from the file host, with no cookie: the file host is not
    // claude.ai and the token in its address is all it takes. Bytes come
    // back as base64, the one way binary survives leaving a page.
    const string Omit = """
        (async () => { const a = ARGS;
          try {
            const r = await fetch(a.path, {credentials: "omit", cache: "no-store", referrerPolicy: "no-referrer"});
            if (!r.ok) { chrome.webview.postMessage({id: a.id, status: r.status, body: ""}); return; }
            const bytes = new Uint8Array(await r.arrayBuffer()); let s = "";
            for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            chrome.webview.postMessage({id: a.id, status: r.status, body: btoa(s)});
          } catch (e) { chrome.webview.postMessage({id: a.id, status: 0, body: ""}); }
        })();
        """;

    public async Task<(int Status, string Body)> Run(string method, string path, string? body, Dictionary<string, string> headers, bool omit = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var done = new TaskCompletionSource<(int, string)>();
        pending[id] = done;
        var args = new JsonObject
        {
            ["id"] = id, ["method"] = method, ["path"] = path, ["body"] = body,
            ["headers"] = new JsonObject(headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
        }.ToJsonString();
        await view.ExecuteScriptAsync((omit ? Omit : Same).Replace("ARGS", args));
        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != done.Task) { pending.Remove(id); return (0, "timeout"); }
        return done.Task.Result;
    }
}

public sealed class SignedOutException(WebAccount account) : Exception(S.F("{0} needs to sign in again.", account.Name))
{
    public WebAccount Account { get; } = account;
}

/// One account's hidden claude.ai page, in that account's own browser
/// profile, loaded with `/api/account` and nothing else.
sealed class WebSession : IDisposable
{
    public WebAccount Account { get; }
    readonly Form host = new() { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) };
    CoreWebView2Controller? controller;
    PageFetch? fetch;
    Task? starting;

    public WebSession(WebAccount account) => Account = account;

    public static Task<CoreWebView2Environment> EnvironmentFor(Guid id)
    {
        Directory.CreateDirectory(WebStore.ProfileOf(id));
        return CoreWebView2Environment.CreateAsync(null, WebStore.ProfileOf(id));
    }

    Task Ready() => starting ??= Start();

    async Task Start()
    {
        _ = host.Handle;
        var environment = await EnvironmentFor(Account.Id);
        controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
        controller.IsVisible = false;
        var core = controller.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        fetch = new PageFetch(core);
        var loaded = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => loaded.TrySetResult();
        core.Navigate(ArtifactApi.Origin + ArtifactApi.AccountPath);
        await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// A 401 or 403 is a sign-in that has lapsed, said as such.
    public async Task<(int Status, string Body)> Send(string method, string path, string? body = null, Dictionary<string, string>? headers = null)
    {
        await Ready();
        var all = ArtifactApi.HeadersFor(path);
        foreach (var (k, v) in headers ?? []) all[k] = v;
        if (body != null) all["Content-Type"] = "application/json";
        var answer = await fetch!.Run(method, path, body, all);
        if (answer.Status is 401 or 403) throw new SignedOutException(Account);
        return answer;
    }

    public async Task<(int Status, byte[] Data)> Download(string url)
    {
        await Ready();
        var (status, body) = await fetch!.Run("GET", url, null, [], omit: true);
        try { return (status, string.IsNullOrEmpty(body) ? [] : Convert.FromBase64String(body)); }
        catch { return (status, []); }
    }

    public async Task<ArtifactApi.Identity?> Identity()
    {
        var (status, body) = await Send("GET", ArtifactApi.AccountPath);
        return status == 200 ? ArtifactApi.ParseIdentity(body) : null;
    }

    public void Dispose()
    {
        try { controller?.Close(); } catch { }
        host.Dispose();
    }
}

/// claude.ai's own sign-in page, in a window of Claude Graft's, writing into
/// a new account's own browser profile. claude.ai never says a sign-in is
/// done, so the window asks `/api/account` whenever the page's address
/// changes (signing in by emailed code ends inside the page already loaded,
/// which finishes no navigation) and every two seconds besides.
sealed class SignInWindow : Form
{
    readonly Microsoft.Web.WebView2.WinForms.WebView2 view = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 };
    readonly Guid id;
    PageFetch? fetch;
    bool asking;
    public ArtifactApi.Identity? Result { get; private set; }

    public SignInWindow(Guid id)
    {
        this.id = id;
        Text = S.T("Sign in to claude.ai");
        Size = new Size(Ui.Theme.S(520), Ui.Theme.S(720));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Theme.Window;
        var hint = new Ui.TextLabel(S.T("Choose “Continue with email” and type the code claude.ai sends you. It is the one way of signing in Claude Graft supports."),
                                    Ui.Theme.Small, Ui.Theme.SubText)
        { Dock = DockStyle.Top, AutoSize = false, Height = Ui.Theme.S(44), Padding = new Padding(Ui.Theme.S(12), Ui.Theme.S(8), Ui.Theme.S(12), 0) };
        hint.MaximumSize = new Size(Ui.Theme.S(490), 0);
        Controls.Add(view);
        Controls.Add(hint);
        HandleCreated += (_, _) => Ui.Theme.Chrome(this);
        Load += async (_, _) => await Begin();
        poll.Tick += async (_, _) => await Check();
        FormClosed += (_, _) => { poll.Dispose(); view.Dispose(); };
    }

    async Task Begin()
    {
        try
        {
            var environment = await WebSession.EnvironmentFor(id);
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            fetch = new PageFetch(core);
            core.SourceChanged += async (_, _) => await Check();
            core.NavigationCompleted += async (_, _) => await Check();
            core.Navigate(ArtifactApi.SignInUrl);
            poll.Start();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, S.F("The sign-in window could not start: {0}", e.Message), Text);
            Close();
        }
    }

    async Task Check()
    {
        if (asking || fetch == null || Result != null) return;
        if (!view.CoreWebView2.Source.StartsWith(ArtifactApi.Origin, StringComparison.OrdinalIgnoreCase)) return;
        asking = true;
        try
        {
            var (status, body) = await fetch.Run("GET", ArtifactApi.AccountPath, null, []);
            if (status != 200) return;
            Result = ArtifactApi.ParseIdentity(body);
            if (Result == null)
            {
                Diagnostics.Note("artifacts.signin-unreadable", "keys: " + string.Join(",",
                    (JsonNode.Parse(body) as JsonObject)?.Select(p => p.Key) ?? []));
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch { }
        finally { asking = false; }
    }
}
