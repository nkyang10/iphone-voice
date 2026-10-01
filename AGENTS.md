# AGENTS.md

Notes for working in this repository. Read this before changing code.

## What this is

An iOS Safari page captures dictation with the Web Speech API and POSTs each finished
utterance to a Windows desktop app, which types it into the focused window with
`SendInput`/`KEYEVENTF_UNICODE`. A global hotkey gates whether typing happens.

Two halves, one repo:

- `web/index.html` — the page. No build step, no dependencies, no framework.
- `desktop/DictationBridge.cs` — the desktop app. Single C# file, compiled by `csc.exe`.

## Build

```powershell
.\build.ps1
```

That is the only supported build command. It compiles with the `csc.exe` shipped in
`Microsoft.NET\Framework64\v4.0.30319` and embeds `web/index.html` as a resource named
`DictationBridge.page.html`.

Do not invoke `csc.exe` by hand. Without the `/resource` flag you get an exe that
silently loses the embedded page and falls back to reading a file from disk.

The language level is **C# 5**. No interpolated strings, no `out var`, no expression
bodied members, no pattern matching, no tuples. `csc.exe` 4.0 rejects them.

## Language split

Do not use a modern C# feature, and do not use a modern JS feature either. The page runs
on the Safari version shipped with whatever iOS the user has, which may be years behind
the newest syntax. Use `var`, `function`, promises, and nothing newer.

## Layout rules

- One `.cs` file. It is a single-file app by design; splitting it up buys nothing at
  this size and complicates the build.
- No NuGet, no npm, no third-party anything. The exe's only references are `mscorlib`,
  `System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`.
- The desktop UI is a borderless `TopMost` form, not a normal window:
  `FormBorderStyle.None` and `ShowInTaskbar = false`, with a one-pixel transparent
  shadow panel so the edge is not invisible. Keep it that way; a caption or a
  taskbar button defeats the point of a floating helper.
- Everything below the strip must be a child of `_detail`, never of `_body`.
  Adding the bottom bar to `_body` left its buttons drawn on top of the collapsed
  strip, where they swallowed clicks aimed for the expand button and closed the
  app instead. `SetExpanded` clears `Enabled` as well as `Visible` for the same
  reason.
- The form's width is pinned with `MinimumSize`/`MaximumSize`. Absolute `Location`
  values inside a docked panel do not shrink that panel's minimum size, so a child
  extending past the client width makes WinForms grow the form and every size
  constant stops meaning anything. Use `TableLayoutPanel` or `Dock` for the rows.
- Only the strip drags. Mouse-move goes to whichever control took the mouse down,
  so the form's own `OnMouseMove` never fires; each draggable control handles
  `MouseMove` itself and sets `Capture`. Save the position on drop, not on exit,
  so a crash or a task-manager kill does not lose it.
- A `Close()` from a panel button is swallowed by the hide-to-tray `FormClosing`
  handler. Raise `QuitRequested` and let the tray context set `_reallyQuitting`;
  that is the only path that actually exits.
- `MainForm`'s fields are deliberately **not** `readonly`: they are assigned in
  `BuildUi`, which the constructor calls, but C# requires the assignment to be
  inside a constructor body.
- Rebinding the hotkey must unregister before registering. `RegisterHotKey` keeps
  an existing registration alive if called twice with the same id, so
  `HotkeyWindow.TryBind` unregisters first and restores the previous binding if the
  new one is refused. F12 is refused; the debugger reserves it.
- `Keys.ToString()` produces names like `Oem7` or `D1` that are meaningless in a
  shortcut label. Use `HotkeyWindow.KeyName`.
- The page must stay dependency-free. It is served to a phone over a LAN.

## Things that will bite you

**The recognizer must be retired before it is aborted.** `abort()` fires that
instance's `onend`, and `onend` schedules another restart. Skip the retire step and each
cycle spawns two recognizers until `start()` throws and dictation dies permanently
after any idle period. This was a real bug; see `rearm()`.

**Every recognizer handler checks `r.retired` first.** Including `onstart`,
`onresult`, `onerror`, `onend`. A retired instance must not touch shared state.

**Never add an `<audio>` or `<video>` element to the page.** WebKit bug 321436: once
media playback has occurred in a session, `SpeechRecognition` can hang with no
`onresult`, `onerror`, or `onend` — no way to detect it programmatically. There is
deliberately no keep-awake audio trick for the same reason; use Auto-Lock to Never.

**Never use `speechSynthesis` or a concurrent `getUserMedia` track.** On iOS the
recognizer goes deaf while TTS plays, and a second audio track gets muted within a
second or two. No read-back confirmations, no level meter.

**The socket port is the page's port.** The server serves the page, `/status`, `/speak`,
and `/diag` on one port, and `/config` returns `wsPort` — an artifact of the design that
is no longer a separate listener. Do not reintroduce a second TLS listener: iOS accepted
TLS on the HTTP port while rejecting the identical certificate on a second port.

**Do not switch back to WebSocket.** It was removed deliberately. Chrome takes the
`Sec-WebSocket-Accept` value verbatim, so a space after the colon fails the handshake;
Node trims it, so every Node test passed while the browser failed. HTTP has no handshake
to get wrong.

**`interimResults = true` is deliberate.** Safari sometimes never sets `isFinal`. With
final-only results you get no output at all, forever.

**A language error must not loop.** `language-not-supported` walks a fallback chain and
stops with a visible message. The original code retried the same rejected code silently,
which presented as "nothing heard, no error".

**Logging escapes non-ASCII.** `Bridge.Describe` writes `\uXXXX`. The log is read in
consoles with arbitrary code pages, and a raw Cantonese string gets mangled there even
though the buffer and the injected text are correct. Keep the exact text where it
matters, escape it only for display.

**The cert is self-signed on purpose.** A CA hierarchy was tried and reverted: `SslStream`
on Windows refuses to serve a chain it cannot validate to a root in a local trust store,
and installing that root on a user's PC is not this app's decision to make. iOS has no
"proceed anyway" button, so the install is unavoidable; do not pretend otherwise.

## Testing

There is no test framework, and adding one is not worth it at this size. Verify by
running things:

```powershell
# does it build and start
.\build.ps1
.\desktop\DictationBridge.exe --port 8099

# do the endpoints behave
node -e "const https=require('https');https.get({host:'127.0.0.1',port:8099,path:'/config',rejectUnauthorized:false},r=>{let b='';r.on('data',d=>b+=d);r.on('end',()=>console.log(r.statusCode,b.trim()))})"
```

For the page, drive real Chrome over the DevTools Protocol with `--headless=new
--ignore-certificate-errors --remote-debugging-port=9333`, and stub
`window.SpeechRecognition` before the page script runs. That is the only way to exercise
language fallback and the idle-then-speak path. Use a real client, not a hand-rolled
WebSocket, or you will not catch browser-specific rules.

`--ignore-certificate-errors` is required because the certificate is self-signed.

## Security posture

Treat this as a personal tool on a trusted LAN. The token in `/config` is the only
barrier, and it travels over TLS to whoever loads the page. Do not describe it as secure,
and do not add features that assume it is.

`dictation-bridge.pfx` holds the certificate private key and is gitignored. Never commit
it. `desktop/index.html` is generated by `build.ps1` and is gitignored; `web/index.html`
is the source of truth.

**The certificate is preserved between runs.** `Certs.Ensure` reuses the saved
`.pfx` unless it is near expiry or stops covering the current address, so a
restart never asks the phone to trust anything new. The SAN carries
`dictation-bridge.local` as well as every current IP, so a phone reached by
that name keeps working on any network. A CA-signed chain is not an option:
`SslStream` refuses to serve one whose root is not in a local trust store,
and installing that root is not this app's decision to make.

**Never let a stale page survive a rebuild.** iOS Safari re-serves a cached copy
even with `no-store`, so a phone left open across a rebuild would keep running the
old script. Send `no-store, no-cache, must-revalidate, max-age=0` plus `Pragma`
and `Expires`, and have the page compare `/config`'s `version` against
`localStorage` and reload itself when they differ.

**`build.ps1` stops a running app before compiling.** Writing the exe fails with
`CS0016` while a copy is running and the previous binary is left in place, so a
"successful" rebuild can still serve the old page and make a version check look
broken. Verify the embedded page really changed after a rebuild.

Before committing, check for secrets in the staged blobs, not just the working tree.
