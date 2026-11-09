using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using ClaudeGraft.Core.Web;
using Microsoft.Web.WebView2.Core;

namespace ClaudeGraft.Web;

/// Exporting an artifact as claude.ai's own menu does, made on this PC:
/// Markdown, PDF and Word from the page as drawn (an artifact that builds
/// itself with script has almost nothing in its source), and the source
/// itself. Google Docs and Notion are connections to other services, not
/// files, and are left out, as on macOS.
static class ArtifactExport
{
    public enum Format { Markdown, Pdf, Word, Source }

    public static string Label(Format f) => f switch
    {
        Format.Markdown => "Markdown",
        Format.Pdf => "PDF",
        Format.Word => "Word",
        _ => S.T("Page source"),
    };

    static string Safe(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "artifact" : clean.Length > 80 ? clean[..80] : clean;
    }

    public static async Task<string?> Run(WebCenter center, Artifact artifact, Format format, IWin32Window owner)
    {
        var (folder, title, problem) = await center.FetchFiles(artifact);
        if (folder == null) return problem ?? S.T("The artifact's files could not be read.");
        try
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            var single = files.Length == 1;
            var (extension, filter) = format switch
            {
                Format.Markdown => (".md", "Markdown (*.md)|*.md"),
                Format.Pdf => (".pdf", "PDF (*.pdf)|*.pdf"),
                Format.Word => (".doc", "Word (*.doc)|*.doc"),
                _ => single ? (".html", "HTML (*.html)|*.html") : (".zip", "ZIP (*.zip)|*.zip"),
            };
            using var save = new SaveFileDialog { FileName = Safe(title ?? artifact.Title) + extension, Filter = filter, OverwritePrompt = true };
            if (save.ShowDialog(owner) != DialogResult.OK) return null;

            if (format == Format.Source)
            {
                // A page of several files goes out zipped: its page alone
                // would open with pieces missing.
                if (single) File.Copy(files[0], save.FileName, overwrite: true);
                else
                {
                    if (File.Exists(save.FileName)) File.Delete(save.FileName);
                    ZipFile.CreateFromDirectory(folder, save.FileName);
                }
                return null;
            }

            using var renderer = new Renderer(folder);
            await renderer.Load();
            switch (format)
            {
                case Format.Pdf:
                    await renderer.Pdf(save.FileName);
                    break;
                case Format.Markdown:
                    await File.WriteAllTextAsync(save.FileName, await renderer.Script(MarkdownScript), new UTF8Encoding(false));
                    break;
                case Format.Word:
                    var html = await renderer.Script(WordScript);
                    var word = "<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w=\"urn:schemas-microsoft-com:office:word\">" +
                               html[(html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? html.IndexOf('>', i) + 1 : 0)..];
                    await File.WriteAllTextAsync(save.FileName, word, new UTF8Encoding(true));
                    break;
            }
            Diagnostics.Note("artifacts.export", format.ToString());
            return null;
        }
        catch (Exception e) { return e.Message; }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    /// Renders a sample artifact that builds its page with script, offline,
    /// and writes it out in the three drawn formats: what `--selftest-web`
    /// checks without any account.
    internal static async Task<List<string>> SelfTest(string outDir)
    {
        var folder = Path.Combine(outDir, "sample");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), """
            <!doctype html><html><head><title>Sample artifact</title><link rel="stylesheet" href="style.css">
            <script src="https://example.com/never-loaded.js"></script></head>
            <body><div id="root"></div><script src="app.js"></script></body></html>
            """);
        File.WriteAllText(Path.Combine(folder, "style.css"), "h1 { color: #c2410c; } .hidden { display: none; }");
        File.WriteAllText(Path.Combine(folder, "app.js"), """
            document.getElementById("root").innerHTML =
              "<h1>Built by script</h1><p>A <strong>bold</strong> word and a <a href='https://claude.ai'>link</a>.</p>" +
              "<ul><li>one</li><li>two</li></ul><table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>" +
              "<p class='hidden'>never shown</p>";
            """);
        var written = new List<string>();
        using var renderer = new Renderer(folder);
        await renderer.Load();
        var md = Path.Combine(outDir, "sample.md");
        await File.WriteAllTextAsync(md, await renderer.Script(MarkdownScript));
        written.Add(md);
        var doc = Path.Combine(outDir, "sample.doc");
        await File.WriteAllTextAsync(doc, await renderer.Script(WordScript));
        written.Add(doc);
        var pdf = Path.Combine(outDir, "sample.pdf");
        await renderer.Pdf(pdf);
        written.Add(pdf);
        return written;
    }

    /// Walks the drawn page the way claude.ai's export walks a source:
    /// headings, paragraphs, lists, tables, quotes and code, leaving out
    /// scripts, styles, drawings, controls and anything hidden.
    const string MarkdownScript = """
        (() => {
          const skip = new Set(["SCRIPT","STYLE","NOSCRIPT","SVG","CANVAS","BUTTON","INPUT","SELECT","TEXTAREA","IFRAME","TEMPLATE","NAV"]);
          const hidden = el => { const s = getComputedStyle(el); return s.display === "none" || s.visibility === "hidden"; };
          const inline = n => {
            if (n.nodeType === 3) return n.textContent.replace(/\s+/g, " ");
            if (n.nodeType !== 1 || skip.has(n.tagName) || hidden(n)) return "";
            const t = [...n.childNodes].map(inline).join("");
            switch (n.tagName) {
              case "STRONG": case "B": return t.trim() ? `**${t.trim()}**` : "";
              case "EM": case "I": return t.trim() ? `*${t.trim()}*` : "";
              case "CODE": return t ? "`" + t + "`" : "";
              case "A": { const h = n.getAttribute("href"); return h && !h.startsWith("#") && /^https?:/.test(n.href) ? `[${t.trim()}](${n.href})` : t; }
              case "BR": return "\n";
              case "IMG": return n.alt ? `![${n.alt}]` : "";
              default: return t;
            }
          };
          const block = (n, out) => {
            if (n.nodeType === 3) { const t = n.textContent.trim(); if (t) out.push(t); return; }
            if (n.nodeType !== 1 || skip.has(n.tagName) || hidden(n)) return;
            const tag = n.tagName;
            if (/^H[1-6]$/.test(tag)) { const t = inline(n).trim(); if (t) out.push("#".repeat(+tag[1]) + " " + t); return; }
            if (tag === "P") { const t = inline(n).trim(); if (t) out.push(t); return; }
            if (tag === "PRE") { out.push("```\n" + n.innerText.replace(/\n$/, "") + "\n```"); return; }
            if (tag === "UL" || tag === "OL") {
              let i = 1; const items = [];
              for (const li of n.children) if (li.tagName === "LI" && !hidden(li)) items.push((tag === "OL" ? (i++) + ". " : "- ") + inline(li).trim());
              if (items.length) out.push(items.join("\n")); return;
            }
            if (tag === "BLOCKQUOTE") { out.push(inline(n).trim().split("\n").map(l => "> " + l).join("\n")); return; }
            if (tag === "HR") { out.push("---"); return; }
            if (tag === "TABLE") {
              const rows = [...n.rows].map(r => "| " + [...r.cells].map(c => inline(c).trim().replace(/\|/g, "\\|")).join(" | ") + " |");
              if (rows.length) { rows.splice(1, 0, "|" + " --- |".repeat(n.rows[0].cells.length)); out.push(rows.join("\n")); }
              return;
            }
            const blocks = [...n.childNodes].some(c => c.nodeType === 1 && !skip.has(c.tagName) && getComputedStyle(c).display !== "inline");
            if (!blocks) { const t = inline(n).trim(); if (t) out.push(t); return; }
            for (const c of n.childNodes) block(c, out);
          };
          const out = []; block(document.body, out);
          const head = document.title && !(out[0] || "").startsWith("# ") ? "# " + document.title + "\n\n" : "";
          return head + out.join("\n\n") + "\n";
        })()
        """;

    /// The drawn page for Word: its styles kept inline, and scripts, frames
    /// and anything from the web taken out, since Word's importer would go
    /// and fetch them while everything waited.
    const string WordScript = """
        (() => {
          let css = "";
          for (const sheet of document.styleSheets) { try { for (const r of sheet.cssRules) css += r.cssText + "\n"; } catch (e) {} }
          const d = document.documentElement.cloneNode(true);
          d.querySelectorAll("script,iframe,object,embed,noscript,link,meta[http-equiv],style").forEach(e => e.remove());
          d.querySelectorAll("[src],[href],[srcset]").forEach(e => {
            // What Word would fetch goes; a link to click is kept.
            for (const a of e.tagName === "A" ? [] : ["src", "href", "srcset"]) { const v = e.getAttribute(a); if (v && /^(https?:)?\/\//i.test(v)) e.removeAttribute(a); }
          });
          const style = document.createElement("style"); style.textContent = css;
          (d.querySelector("head") || d).appendChild(style);
          return "<!DOCTYPE html>" + d.outerHTML;
        })()
        """;

    /// A browser with no sign-in in it, never shown, serving the artifact's
    /// folder under a name of its own and refusing every request that would
    /// leave it. Its profile is thrown away afterwards.
    sealed class Renderer(string folder) : IDisposable
    {
        const string Host = "artifact.claudegraft.invalid";
        readonly string profile = Path.Combine(Path.GetTempPath(), "ClaudeGraft-render-" + Guid.NewGuid().ToString("N")[..8]);
        readonly Form host = new() { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) };
        CoreWebView2Controller? controller;
        CoreWebView2Environment? environment;

        public CoreWebView2 Core => controller!.CoreWebView2;

        public async Task Load()
        {
            _ = host.Handle;
            environment = await CoreWebView2Environment.CreateAsync(null, profile);
            controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
            controller.Bounds = new Rectangle(0, 0, 816, 1056);
            controller.IsVisible = false;
            Core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Deny);
            Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            Core.WebResourceRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || u.Host != Host)
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            Core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Host != Host) e.Cancel = true;
            };
            Core.NewWindowRequested += (_, e) => e.Handled = true;
            var loaded = new TaskCompletionSource();
            Core.NavigationCompleted += (_, _) => loaded.TrySetResult();
            Core.Navigate($"https://{Host}/index.html");
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            // An artifact that builds itself with script is given a moment.
            await Task.Delay(1200);
        }

        public async Task<string> Script(string script)
        {
            var json = await Core.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }

        /// Letter width, as one tall page.
        public async Task Pdf(string path)
        {
            var height = double.TryParse(await Core.ExecuteScriptAsync("document.documentElement.scrollHeight"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) ? px : 1056;
            var settings = environment!.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.PageWidth = 8.5;
            settings.PageHeight = Math.Max(11, height / 96.0 + 0.5);
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.4;
            if (!await Core.PrintToPdfAsync(path, settings)) throw new IOException(S.T("The PDF could not be made."));
        }

        public void Dispose()
        {
            try { controller?.Close(); } catch { }
            host.Dispose();
            var dir = profile;
            Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++) { try { Directory.Delete(dir, true); return; } catch { await Task.Delay(1000); } }
            });
        }
    }
}
