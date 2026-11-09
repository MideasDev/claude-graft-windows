<div align="center">

<img src="docs/icon.png" width="96" alt="Claude Graft for Windows">

<h1>Claude Graft for Windows</h1>

<p><b>One Claude login is never the right number.</b><br>
All your Claude accounts, at once, each in its own profile.</p>

<p><a href="README.es.md">Leer en español</a></p>

</div>

Claude Graft for Windows is a Windows version of
[Claude Graft](https://github.com/aaditya-v-more/claude-graft) by Aaditya More,
which lives in the macOS menu bar. Here it lives in the notification area: it
runs several copies of Claude Desktop side by side, each with its own sign-in,
chats and settings, and tells you how much usage each account has left.

## Installing

You need Windows 10 1809 or later, [Claude Desktop](https://claude.ai/download)
(the Microsoft Store version or the classic installer) and the
[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0),
unless you use the self-contained build.

Download `ClaudeGraft-<version>-win-x64.zip` from
[Releases](https://github.com/MideasDev/claude-graft-windows/releases), unpack
`ClaudeGraft.exe` into a folder of your own (for example
`%LOCALAPPDATA%\Programs\ClaudeGraft`) and open it. It is not signed, so
SmartScreen may ask you to confirm the first time (**More info › Run anyway**).

To build it yourself:

```powershell
.\build.ps1              # tests + dist\ClaudeGraft.exe
.\build.ps1 -SelfContained -Zip
```

The interface follows Windows' display language: Spanish on a Spanish
Windows, English everywhere else.

## One Claude per account

**New Claude** creates a profile in `%LOCALAPPDATA%\ClaudeGraft\Profiles\<folder>`
and, if you like, Desktop and Start menu shortcuts with their own icon and
colour. You sign in once and it stays signed in. Each Claude has its own
chats, MCP servers and settings; **Copy MCP servers from main**, in the **⋯**
menu on its card, adds the ones your usual Claude has, without touching those
it already had.

Opening a Claude that is already open brings it to the front: two processes
never run on the same profile, since both would write the same chat files. A
shortcut to a Claude you have removed opens no other in its place.

## Artifacts, projects and chats across accounts

In the sidebar, **Artifacts**, **Projects** and **Search Chats** gather what
all your accounts have in one place. Each account signs in to claude.ai once,
in a Claude Graft window, with the code sent by email, and that session lives
in a browser of that account's own: Claude Graft never reads its cookie.
Artifacts can be opened, shared by link, with people or with a group (each
person with permission to view, comment or edit), renamed, duplicated,
deleted, and exported to Markdown, PDF, Word or their source code. Search
finds chats and Code sessions, and only asks claude.ai when you press Enter or
Search.

## Shared usage

When one account runs out, Claude Code carries on with another's quota
without leaving the Claude you are in. In its **Shared usage** section, **Add
another account** brings in other Claudes, or signs in with another account
that only lends its quota. Only the model's replies come from the other
account: your chats, files and connectors stay where they are, and the Chat
tab is never shared.

The account whose week resets soonest and still has room answers; a
conversation stays with the account that started it while its cache lasts,
and from the notification-area menu you can move any recent conversation to
another account. It works through a small relay on `127.0.0.1:47823` with a
certificate that can only answer for `api.anthropic.com`; Claude Graft starts
and stops it, and configures Claude Code while that Claude is closed, so
changes apply the next time it opens.

## Sharing chats

A new Claude starts with its own chats. Under **Reads chats from** you can have
it read your main Claude's chats or another Claude's: it gets its own copy of
those chats, and each time either one opens, changes travel both ways (a new
chat, a renamed one, a deleted one). Its own chats are added to the other's
too, and that merge is the one thing that cannot be undone; going back to
**Its own chats** leaves it with exactly its own, after handing the other what
you did in the meantime. It works between two profiles of the same account and
between different accounts.

Pinned Code chats, their order and how they are sorted are synchronised
between the two as well, just before one opens, as long as every Claude
sharing those chats is closed.

If you open a Claude while another with the same chats is open, Claude Graft
warns you first, because opening the same conversation in two at once can lose
messages. And if switching accounts inside a Claude left its chats in another
profile, the window tells you where they are and lets you copy them.

## The notification area

Clicking the icon opens a panel, like the original's menu: each Claude with
its usage and an **Open** button, the shared-usage conversations, the **Start
with Windows** and live-usage switches, and the actions. Right-click opens a
short menu. The window follows the original's layout: the Claudes in a
sidebar and, on the right, the chosen one in cards.

The icon takes the colour of the Claude in front of you and shows its
account's usage: the five-hour window's percentage, or the week's (with a bar
underneath) for accounts that only have a weekly limit. The menu lists every
Claude, which are open and how much each has used, with the time until it
resets in the tooltip. A click opens or brings forward any of them.

The figures come from the history each Claude writes while it is open
(`plan-usage-history.json`). Turn on **Read live usage from Anthropic** and
they are also asked of the same service Claude Code uses, so they are current
even if that Claude has been closed for days.

## Updates

Every hour Claude Graft checks whether there is a new Claude Desktop, under an
identifier of its own and without using any account, and says so at the
bottom of the window and in the icon's panel. With **Update Claude Desktop
manually** (in Settings) no Claude updates itself, so no update ever closes
work in progress; the new version installs from that line, with every Claude
closed (Claude Graft never closes one for you), after its SHA-256 is checked.
The Microsoft Store version is updated by the Store.

Claude Graft updates itself from
[this repository's releases](https://github.com/MideasDev/claude-graft-windows/releases):
it downloads the zip, checks its SHA-256, swaps the executable and restarts,
quietly, unless you turn on **Hold Claude Graft updates**.

To publish a version: raise the number in `Directory.Build.props`, commit, and
push a tag with that number (`git tag v0.2.0` and `git push origin v0.2.0`).
GitHub builds it, runs the tests and publishes the zip with its `.sha256`.

## Safe by design

Claude Graft only touches what it created: its profiles, its icons and the
shortcuts it made itself. Claude's own profile is never renamed, moved or
deleted; removing a Claude sends its folder to the Recycle Bin, never deletes
it outright, and only if it lies inside the profiles folder. A list that
cannot be read is never overwritten.

Live usage borrows each profile's sign-in without keeping it: it decrypts
only the access token, with your own Windows account's protection (DPAPI),
never the refresh token, writes nothing back, and sends it only to
`api.anthropic.com`. It is off until you turn it on.

## Differences from the macOS version

This version brings over what the original does: several Claudes at once,
shortcuts, shared chats and pinned chats, shared usage, each account's usage in
the notification area, opening without duplicating, and updates for Claude and
for itself. A few pieces answer problems macOS has and Windows does not, and
are left out on purpose: the sweep that files missing Code session records,
and the recovery after an updater's relaunch.
[docs/PORTING.md](docs/PORTING.md) explains why, and how each piece was done
on Windows.

Windows groups every Claude window under one taskbar icon, since they are all
the same app; the notification-area icon tells you which one is in front.

## Help

`ClaudeGraft.exe --report` writes `%LOCALAPPDATA%\ClaudeGraft\state-report.txt`
with what the program sees (where Claude is, which Claudes are open, the usage
figures), and `diagnostics.log`, in the same folder, records what it has done.
Neither contains tokens or chats.

[MIT](LICENSE). Based on [Claude Graft](https://github.com/aaditya-v-more/claude-graft)
by Aaditya More; if you find it useful, the original's
[tip jar](https://ko-fi.com/aadityavmore) keeps it going.
