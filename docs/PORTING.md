# Porting notes

How each part of Claude Graft for macOS was carried to Windows, what was
measured on a real machine to decide it, and what is not here yet. Read the
section for an area before changing its code.

## Layout

| Here | macOS original |
|---|---|
| `src/ClaudeGraft.Core` | `Sources/Shared/GraftCore.swift`, `UsageAPI.swift`, `ClaudeCredentials.swift`, `UsageMonitor.swift` |
| `src/ClaudeGraft` (WinForms) | `Sources/App` (SwiftUI, menu bar) |
| `ClaudeGraft.exe --open <folder>` | `Sources/Launcher` (the binary inside each shortcut bundle) |
| `tests/ClaudeGraft.Tests` | `Tests/main.swift` |
| `build.ps1`, `test.ps1` | `build.sh`, `test.sh` |

## Opening a profile

**Claude for Windows honours `--user-data-dir`.** Measured on Claude 4.23118
(Store build, Electron 44): `claude-desktop.exe --user-data-dir="<folder>"`
starts a second, fully separate Claude beside the one already open, and every
one of its helper processes repeats the flag.

**The Store build is started through its execution alias.** An MSIX app
cannot be run from `WindowsApps` directly, but it registers
`%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe`, which takes
arguments like any program. The classic installer's
`%LOCALAPPDATA%\AnthropicClaude\claude.exe` is the fallback.

**The main profile is recognised by having no `--user-data-dir`.** The Store
build keeps its data in `%LOCALAPPDATA%\Claude-Data\claude_<publisher>` and
passes that folder to its helpers, but its browser process carries no flag at
all, exactly as on macOS.

**The browser process is the one without `--type=`.** Only it owns the
windows, so only its pid can be revealed. Paths are compared whole and
normalised, never as prefixes, so `Claude-2` never matches `Claude-22`.

**Claude Code is a `claude.exe` too.** The desktop app runs copies of it from
`Claude-Data\…\claude-code\<version>\claude.exe`, and on this machine they were
taken for two extra main Claudes. A Claude Desktop process is one whose
executable sits beside `resources\app.asar`.

**Command lines come from `NtQueryInformationProcess`**
(`ProcessCommandLineInformation`), not WMI. Late-bound WMI from .NET failed
with `E_FAIL` on most rows of the very same query PowerShell answered in full.

**Opening a profile that is already open shows the Claude that is there.**
Claude takes no single-instance lock, so a second process on one profile
would start and write the same chat store. `Opener` asks first and reveals:
the window still exists when Claude has been closed to the notification area,
so `ShowWindow` and `SetForegroundWindow` bring it back.

**The launcher starts Claude through the shell.** With `CreateProcess` and
inherited handles, the new Claude held the launcher's pipe and the terminal
that ran the launcher waited for as long as that Claude ran. The variables a
Claude Code session sets (`CLAUDECODE`, `CLAUDE_CODE_*`) are removed from the
launcher's own environment first, so a Claude opened from inside a session
does not think it is part of it.

**A missing shortcut is never permission to open another profile.**
`--open <folder>` for a folder not in the list reports that and opens nothing.

**A shortcut behaves like the Claude Graft installed now.** A `.lnk` is just
this executable with `--open <folder>`. At start, `ShellLinks.Refresh` points
every recorded `.lnk` at the current executable if it moved, and forgets one
the person deleted. Only links recorded in `shortcuts.json` are ever touched.

## Usage and credentials

The history file and the service answer are read exactly as on macOS,
including the rules for an account with no five-hour limit (a single sample
without `fh` is a gap; two running and none in five hours is no limit) and the
polling budget (five minutes a tick, two for an open Claude, a minute for the
menu, two seconds for a press; failures back off 60 s → 30 min; a
`Retry-After` cannot be skipped).

Credentials differ. On Windows Electron's safe storage is Chromium's
`os_crypt`: `Local State` → `os_crypt.encrypted_key`, base64, `DPAPI` prefix,
unsealed with `CryptUnprotectData` for the current user; values are
`v10 | nonce(12) | ciphertext | tag(16)` under AES-256-GCM. There is no
keychain prompt to manage. `config.json`'s `oauth:tokenCacheV2` is read the
same way as on macOS; the suite seals a stand-in profile this way and reads it
back. Reading a real profile's login has not been checked on a real machine
yet, and live usage is off until a person turns it on.

## Sharing chats

`Graft.cs` is the macOS graft, rule for rule; the reasoning behind each rule
is in the original's `notes/chat-sharing.md`, and the suite has a check for
every loss that note describes. The store layout on Windows was confirmed to
be the same: `<store>\<account>\<org>\local_*.json` plus `deleted_*` markers,
`skills-plugin` beside the accounts, in both `claude-code-sessions` and
`local-agent-mode-sessions`.

What differs on Windows:

- **Links are junctions for folders and symbolic links for files.** A
  junction needs no privilege; a file link needs Developer Mode or an
  administrator, and without one the file simply stays the profile's own
  (`link.unsupported` in the log). Chat stores are never linked, on either
  system: they are mirrored, because Claude files no record into a folder
  that resolves outside the profile.
- **A record Windows will not let anyone read is skipped, not taken for
  deleted.** A file being written can refuse readers on Windows, which macOS
  never does; read as absent, it would have been carried across as a
  deletion. `mirror.busy` in the log.
- **Paths are compared as real paths** (`GetFinalPathNameByHandle`), without
  case, so a pair key, "resolves inside the profile" and "the same profile"
  all see through junctions.
- **The organization in use is where the profile's own records are, not
  the newest folder.** Seen on the first real test: an account with two
  organizations, its chats in one and only `scheduled-tasks.json` in the
  other, touched last. The newest-folder rule the original uses sent the
  main Claude's 20 chats into the empty one, which the sidebar never showed.
  `ActiveOrganization` takes the folder holding the newest record of the
  profile's own (one that is not a byte-for-byte copy of the source's), then
  the folder already paired, then the newest folder.
- **File links are removed as files.** With Developer Mode on, file links
  are made, and asking a `DirectoryInfo` about one answers as if it were a
  folder; removal then failed with "the directory name is invalid" and left
  the borrower pointing at the source's settings. A link is now judged by
  the entry's own attributes.
- **Never run from inside a packaged app.** Claude Desktop from the Store is
  an MSIX package, and a process started from it (a Claude Code terminal)
  inherits the package: Windows keeps what it writes under AppData in
  `Packages\Claude_…\LocalCache` and shows that copy back to it, so it sees
  a different list and log from the Claude Graft the person opened. The
  tray app relaunches itself through Explorer when it finds a package
  identity it inherited.
- **No bundle.** A shortcut's chat source lives in `shortcuts.json` alone, so
  the list and what runs cannot disagree, which on macOS needed
  `Installer.refreshConfig`.

The graft runs on the same occasions as on macOS: when a closed Claude is
opened (before it starts, since it builds its sidebar as it starts), when
Claude Graft starts, when the tray menu opens, and when a Claude's chat
source is changed while it is closed. Opening a Claude while another one on
the same chats is open asks first.

## Session records: not ported, on purpose

The macOS sweep (`fileMissingSessionRecords`) writes a record for a Claude
Code session that has a transcript in `~/.claude/projects` and no record in
any sidebar. It exists for one cause: on macOS a grafted profile's chat store
was a link, and Claude writes no record into a folder that resolves outside
the profile, so sessions started there were never filed. This port never
links a chat store (it mirrors), so a grafted profile writes its own records
as any profile does, and the cause is not there.

Nor could the sweep be ported faithfully. It learns which account and
organization a session belongs to from `ownerAccountUuid` on the
transcript's `bridge-session` line, and that line is the only place it is
written. Measured on 9 November 2026 with Claude 4.23118 and Claude Code
2.1.309–2.1.317 on Windows: 27 transcripts, none with a `bridge-session`
line. Filing without it would mean guessing whose sidebar a conversation
goes into, which is the one mistake the original refuses to make. If a later
Claude writes the line again, the sweep can be ported then, with every rule
in the original's `notes/session-records.md`.

## Shared usage

`Core/Pool` is the macOS relay and usage pool, rule for rule: which account
answers (`PoolPolicy`, `PoolState`), what never moves (`PoolRequest`), what
goes out byte for byte (`Exchange`), and the lines in a closed profile
(`PoolEnvironment`). The reasoning for each is in the original's
`notes/shared-usage.md`. Checked on Windows before writing it: Claude
4.23118 keeps `ccd-environment-config.json` with `envVars` as base64 of
`safeStorage.encryptString` over JSON, the same as macOS, and its bundle
lists `HTTPS_PROXY` and `NODE_EXTRA_CA_CERTS` among the variables it lets
through to Claude Code.

What differs on Windows:

- **The relay is `ClaudeGraft.exe --relay`**, started detached through the
  shell so it holds none of Claude Graft's handles and outlives it. Its one
  instance is the port itself, bound exclusively on loopback; its control
  channel is a named pipe opened with `CurrentUserOnly`. A relay built from
  another copy of the program (compared by SHA-256) is asked to step aside:
  it stops listening at once, so the replacement can bind, and leaves when
  its last exchange ends, a quarter of an hour at most.
- **The relay's key lives in the user's key store while the relay runs.**
  SChannel refuses a key held in memory alone ("the platform does not support
  ephemeral keys", measured), so the leaf is loaded with `UserKeySet` and no
  `PersistKeySet`: Windows deletes that copy when the certificate is
  released. At rest it is a PKCS#12 sealed with DPAPI. The authority's key is
  never written at all, and the authority carries a critical
  `nameConstraints` permitting `api.anthropic.com` alone: the suite checks a
  client trusting only it accepts the relay for that name and refuses it for
  any other, and checks the same with Node through `NODE_EXTRA_CA_CERTS`.
- **No keychain, so no journal.** On macOS a launcher could not read the
  safe-storage key without a dialog, so Graft sealed both values beforehand
  and launchers swapped one for the other. On Windows the key opens with
  DPAPI silently, so the launcher settles the lines itself, the moment
  before a closed Claude starts.
- **Accounts signed in from the window** are sealed with DPAPI in
  `usage-sharing.json` rather than with Claude's key.

Checked here: the rules, the relay over loopback against a stand-in service
(lending, passing over a full account and a refused login, holding a
conversation for its cache, a choice from the menu, `thread_not_found`,
chunked answers, stepping aside), the lines written into and out of a
sealed stand-in profile, and the real relay process on port 47823. Not yet
seen: a real Claude Code session answered by a second real account.

## Artifacts, projects and search

`Core/Web/ClaudeWeb.cs` holds the requests and how their answers are read,
as the original read them off claude.ai's own page; `Web/` runs them. What
differs on Windows:

- **WebView2 instead of WKWebView.** Each account has a browser profile of
  its own under `ClaudeGraft\web\<id>`, which the sign-in window writes into
  and a hidden page then reads from. Requests are `fetch` calls run in a
  claude.ai page; a page cannot hand a promise back to the host, so each
  answer comes back through `chrome.webview.postMessage`, matched by id.
  The cookie goes only where the browser engine sends it.
- **Signing out deletes the account's whole profile folder.** The browser
  can hold its files for a moment after closing, so a folder that will not
  go is removed at the next start.
- **Exports render in a throwaway profile**, serving the artifact's folder
  under a name of its own (`SetVirtualHostNameToFolderMapping`) and refusing
  every request that would leave it. PDF is WebView2's own printing at Letter
  width as one tall page. Word is the drawn page as HTML in a `.doc`, which
  Word opens: its styles inline, scripts, frames and anything Word would
  fetch taken out. Markdown walks the drawn page as on macOS.
- **No "where it ran" for Code sessions.** The original tells this Mac's
  sessions from another Mac's by the `bridge-session` line in transcripts,
  which this Claude on Windows does not write (see Session records). Search
  still tells cloud sessions apart by `environment_kind`.

Checked here: every request path and parser against answers shaped like
claude.ai's, and `ClaudeGraft.exe --selftest-web <folder>`, which renders a
sample artifact built by script and exports it in the three drawn formats
offline, and asks claude.ai for the account from a profile with no sign-in
(answered 401, as it should be). Not yet seen: a real sign-in and the lists,
shares and exports of a real account.

## Pinned chats

`Core/Sidebar` is the original's sidebar sync: the merge against the last
agreement, the pass over every Claude connected through a shared history,
only before a closed Claude opens and only with all of them closed, a
backup, a write read back and put back if it does not match, and the
records' `isStarred` brought in line. `sidebar-storage.js` is the original's
helper script, with one change: the profile's storage folders are linked as
junctions, which need no privilege on Windows.

The helper is the same idea as on macOS, a private copy of Claude's own
runtime, so the databases are always opened by exactly the browser version
Claude uses. On Windows it is simpler: `%LOCALAPPDATA%\ClaudeGraft\sidebar-runtime`
holds a copy of the runtime files (not Claude's `resources`), its executable
renamed `graft-sidebar.exe` so it is never taken for a Claude, with its own
`app.asar` and one Electron fuse switched off in the copy: embedded asar
integrity, which would refuse an archive Anthropic did not make. Nothing is
re-signed, and the installed Claude is never changed. The Store build's
folder is read from its execution alias's reparse point. Every launch takes
`Local\ClaudeGraftWindows.Launch`, so two openings never sync at once.

Checked here: the merge and the pass over stand-in profiles (the suite), the
storage script under Node, and the real helper built and run on this
machine against a disposable profile: it started, opened the profile's
storage and refused it as having no sidebar yet (`origins with a sidebar: 0`,
a profile that never signed in), writing nothing. In the profiles here the
pin record (`store:pin-state:dframe-starred-code`, `starredIds`) is present.
Not yet seen: a read and a write of a signed-in profile, which needs one
closed. `ClaudeGraft.exe --sidebar-read <profile> <result file>` reads one
and writes nothing else.

## Updates

**Claude Desktop.** The original runs Claude's own updater (Squirrel.Mac,
ShipIt) in a profile of its own and recovers profiles the relaunch left
closed. On Windows, Claude's updater is Electron's MSIX one, and three
things were measured instead:

- The update feed answers anonymously:
  `api.anthropic.com/api/desktop/win32/<arch>/msix/update?device_id=…&version=…`
  gives the current version, the package's address on `downloads.claude.ai`,
  its SHA-256 and size. Claude Graft asks it with a device identifier of its
  own, at start and hourly, and never with an account.
- The configuration library Claude reads `disableAutoUpdates` from is one
  for every Claude on Windows: Claude's code asks for
  `%LOCALAPPDATA%\Claude-3p\configLibrary` whatever `--user-data-dir` says.
  Manual update mode writes there, journalling only the keys it changes and
  putting them back when turned off, and refuses when
  `HKLM`/`HKCU\SOFTWARE\Policies\Claude` holds a policy or the
  configuration comes from elsewhere, as the original refuses managed
  preferences.
- Installing is Windows' own: the package is downloaded, its size and SHA-256
  checked against the feed, and `Add-AppxPackage` installs it, only with
  every Claude closed. Nothing is ever force quit. Success is the installed
  version changing, not the package being installed. The Store build
  (`AnthropicPBC.Claude_…`) and the classic installer are left to their own
  updaters.

Not ported: the original's update recovery (reopening profiles an updater's
relaunch left closed), which answers ShipIt's relaunch on macOS; with manual
mode on, updates on Windows only ever run with every Claude closed.

**Claude Graft itself** updates from this repository's GitHub releases: the
release's `ClaudeGraft-<version>-win-x64.zip`, checked against the SHA-256
GitHub records for the upload (or the `.sha256` beside it), only from this
repository's download address. The running executable is renamed beside the
new one, which Windows allows where overwriting is refused, and the relay
started from the old one is replaced by the new as on macOS. It installs
itself quietly, never while the window is open, unless held. A tag `v<version>`
matching `Directory.Build.props` builds and publishes a release
(`.github/workflows/release.yml`).
