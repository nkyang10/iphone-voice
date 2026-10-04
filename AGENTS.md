# AGENTS.md

Notes for working in this repository. Read this before changing code.

`README.md` is written for people who want to use the app, not to build it. Keep it that
way: setup steps, the certificate dance, troubleshooting, limits. Anything that only
matters to someone editing the code belongs here instead.

## What this is

An iOS Safari page captures dictation with the Web Speech API and POSTs each finished
utterance to a Windows desktop app, which types it into the focused window with
`SendInput`/`KEYEVENTF_UNICODE`. The page's Start button is the only on/off switch: while
it is listening, every utterance is typed, with no desktop gate.

Two halves, one repo:

- `web/index.html` — the page. No build step, no dependencies, no framework.
- `desktop/DictationBridge.cs` — the desktop app. Single C# file, compiled by `csc.exe`.

The app is distributed as a single self-contained exe. Anyone can run it; they should never
need to read this file.

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

**No test framework.** There is none and adding one is not worth it at this size. Verify by
running things, and by driving real Chrome over the DevTools Protocol with
`--headless=new --ignore-certificate-errors --remote-debugging-port=9333`. Stub
`window.SpeechRecognition` before the page script runs to exercise language fallback and
the idle-then-speak path. Never test with a hand-rolled WebSocket client: it trims header
values that a browser takes verbatim, so it will pass where Safari fails.

## Language split

Do not use a modern C# feature, and do not use a modern JS feature either. The page runs
on the Safari version shipped with whatever iOS the user has, which may be years behind
the newest syntax. Use `var`, `function`, promises, and nothing newer.

## Layout rules

- One `.cs` file of our own, in `desktop/`. It is a single-file app by design; splitting
  it up buys nothing at this size and complicates the build.
- No NuGet, no npm, no package manager. The exe's only references are `mscorlib`,
  `System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`.
- **One vendored library is allowed: QRCoder, under `vendor/QRCoder`.** It supplies the
  QR encoder and is compiled in by `build.ps1`, so nothing is loaded at runtime and the
  exe is still a single file. This is an exception to the no-third-party rule, and the
  reason is in "Things that will bite you" below: the QR code has to be scannable, and a
  hand-written encoder is not a substitute for one that is known to work. Do not add a
  second vendored library without the same argument, and prefer `vendor/` over a
  package reference so the build keeps working offline with the shipped `csc.exe`.
- The desktop UI is a borderless `TopMost` form, not a normal window:
  `FormBorderStyle.None` and `ShowInTaskbar = false`, with a one-pixel transparent
  shadow panel so the edge is not invisible. Keep it that way; a caption or a
  taskbar button defeats the point of a floating helper.
- The palette lives in `Skin`. Everything is painted by `Skin.RoundedPath` and
  the small `Paint*` controls; do not reintroduce stock grey WinForms buttons.
- Never use `TransparencyKey` on this form. It switches the window into a
  layered mode that turns GDI text antialiasing off, so every custom-painted
  string comes out jagged. Round the corners with a `Region` in `OnPaint`
  instead, and keep `Color.Transparent` out of it: a custom-painted control
  or a `TextBox` given that back colour throws "not a valid owner window
  handle" before it is parented, and once parented it still routes through
  the layered path.
- Set `TextRenderingHint = ClearTypeGridFit` in every custom `OnPaint` that
  draws text, or the labels alias even without the transparency key.
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
- There is no armed flag, no buffer and no hotkey. `Bridge.OnText` types
  unconditionally, and `/status` carries `typed`, `received`, `phone` and `last` only.
  Do not reintroduce a desktop gate: the phone's Start button is the switch, and a
  second one on the PC was a duplicate decision, not a safety net. It did mask
  room noise on a phone left face up, which is now a documented trade-off in the
  README rather than something the code hides.
- The Start button's `disabled` state comes from **`syncStartButton()` and nowhere
  else**, and its label never changes -- it always reads "Start listening". It used to
  be written from four event handlers, which is how they came to disagree. iOS
  requires a gesture per `start()`, so Start was made permanently re-pressable as the
  only tap-to-resume affordance; that made the button useless as an indicator, because
  a button that is always live says nothing. It is now disabled while a recognizer is
  live (`rec && recStartedOk && !recEnded && !recFault`), and enabled the moment iOS
  ends the session or reports an error -- the only times a tap can do anything. The
  dot and status line above it carry the detail; the button stays the word you already
  know how to find.
- **A disabled button is not automatically inert.** `startTapped` and `stopTapped`
  both bail on `el.start.disabled` / `el.stop.disabled` first. A synthetic
  `pointerdown` reaches the handler on a disabled control, and iOS Safari is not
  obliged to suppress events the way Chrome does. Letting one through retires a
  healthy recognizer and builds another, which is the recognizer churn this button
  exists to report on.
- The page's `runHeard` is per-Start; the page-lifetime `stats.heard` was what let the
  deaf timer below stay switched off for the rest of the session.
- Text is **streamed, not committed per utterance**, and `Injector.TypeText` is called
  with `appendSpace: false`. The page sends the recognizer's interim results as they
  arrive; the desktop appends each chunk into the focused window. Three consequences
  are load-bearing:
  - `KEYEVENTF_UNICODE` cannot select or delete, so **sent text is final**. The page
    anchors on content (`streamAnchor`), and when Safari rewrites words already sent it
    re-anchors and sends nothing for the rewrite, because there is nothing better to do
    with it.
  - Spacing belongs to the page, because only it knows where a result boundary was.
    `buildFull` inserts a space at a boundary inside one recognizer; `withSeparator`
    handles only the first chunk of a recognizer, which has no left context. Applying a
    separator to every chunk split words in half mid-utterance.
  - One ordered outbox, drained head first. A failed chunk stays at the head and is
    retried by the status poll. The old code pushed failures onto a side list and let the
    next chunk post immediately, which could reorder a sentence.
- The page must stay dependency-free. It is served to a phone over a LAN.

## Things that will bite you

**Never hand-write a QR encoder.** The first version of `QrView` included an
encoder written by hand: Reed-Solomon division, mask selection, the eight
version tables, the placement zig-zag. It produced a clean, symmetric, entirely
plausible-looking 29x29 matrix that no scanner could read, and it was roughly 450
lines. The Reed-Solomon output eventually matched the published test vectors and
the code *still* would not decode, because the bug had moved into a placement or
masking rule. This is why `QrView` now calls the vendored QRCoder and nothing
else. If you ever need to touch the code, verify it round-trips through an
independent decoder rather than by eye.

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
and `/diag` on one port, and the page reaches every endpoint with a relative path, so it
follows whichever port the app ended up on. Do not reintroduce a second TLS listener: iOS
accepted TLS on the HTTP port while rejecting the identical certificate on a second port.

**Do not switch back to WebSocket.** It was removed deliberately. Chrome takes the
`Sec-WebSocket-Accept` value verbatim, so a space after the colon fails the handshake;
Node trims it, so every Node test passed while the browser failed. HTTP has no handshake
to get wrong.

**`interimResults = true` is deliberate.** Safari sometimes never sets `isFinal`. With
final-only results you get no output at all, forever. This is also why interim text is
sent rather than held: the interim path was originally local-only, so an utterance Safari
never finalised never reached the desktop at all, and the fix for the watchdog churn
("an idle recognizer stays open indefinitely") made that path far more reachable.

**Streaming means a misheard word cannot be fixed.** Expect users to report one. The
answer is in the README, and the diagnostic counters are `rewrites` in the phone's report.
Do not add a desktop-side buffer or a select-and-retype path to fix it: that is the gate
this design removed.

**A language error must not loop.** `language-not-supported` walks a fallback chain and
stops with a visible message. The original code retried the same rejected code silently,
which presented as "nothing heard, no error".

**Logging escapes non-ASCII.** `Bridge.Describe` writes `\uXXXX`. The log is read in
consoles with arbitrary code pages, and a raw Cantonese string gets mangled there even
though the buffer and the injected text are correct. Keep the exact text where it
matters, escape it only for display. `crash.txt` is the deliberate exception: it is read
in Notepad and attached to a report, so it keeps raw UTF-8.

**The app can die with nothing to show for it, because it is a `winexe`.** `/target:winexe`
means there is no console when a user double-clicks the exe, so every `Console.WriteLine`
reaches nobody. Worse, anything that throws before the first window exists — a port already
in use, a full disk, a denied folder — used to end the process with a log that simply
stopped mid-sentence. That is not hypothetical: it is what a real user's "it just closes"
report turned out to be. Three things prevent a repeat, and all three are load-bearing:

- `Log.InstallCrashHandlers()` is the first statement in `Main`, before any work. It must
  call `Application.SetUnhandledExceptionMode(CatchException)` first, or WinForms handles a
  UI-thread exception itself and `Application.ThreadException` never fires.
- `Main` wraps the real work in try/catch/finally. Anything that can throw before the
  message loop has to land in that catch, or it is an invisible exit.
- A fatal error also shows a `MessageBox`. With no console it is the only on-screen
  evidence that the app started at all.

Do not add work to `Main` outside that try. `Program.Run` is the body; `Main` is the frame.

**`last-run.txt` is how a crash is recognised after the fact.** A process that dies cannot
clean up, so `BeginRun` writes `state: running` and `EndRun` overwrites it. On the next
launch, a marker still reading `running` is reported as an unclean exit. `EndRun` is called
from a `finally` and is idempotent: without the `finally`, a clean exit would leave the
marker saying `running` and every later launch would report a crash that never happened.
The run counter is carried across launches through that same file, so it has to be written
even when nothing else changes.

**`Log.Quiet` only collapses consecutive repeats, and the key must be stable.** The phone
polls `/status` every second or two; written out in full it buried the startup and crash
lines that were the reason anyone was reading the file. Two consequences: it compares
against only the *previous* line, so two messages that alternate will both be written, and
the request line must not include the client's ephemeral port, or nothing ever matches.
`Servers.Host` strips it.

**The main log rotates at 2 MB.** It did not used to, for the same reason `/status` was
quieted. Anything appended per request needs a `Roll` call, not just the diagnostics file.

**Do not trust `Environment.OSVersion` in this exe.** Without a `supportedOS` manifest,
which is not worth adding because it changes how the app is shelled, Windows reports 6.2 for
every modern release. `Log.WinVersion` reads the build number from the registry instead.
The registry's `ProductName` is *also* wrong in the other direction — it says "Windows 10"
on Windows 11 — so it is reported separately and labelled as the stale field it is.

**The default port is 17123, and the fallback is random.** Both are deliberate, and both came
from a real report. `8080` was the old default and it is the single most contested port on a
Windows machine: half the dev servers ever written default to it, and Hyper-V, WSL2 and
Docker Desktop hand out large reserved blocks that routinely swallow it. `WSAEACCES`
(10013) on a port above 1024 is *not* a conflict — nothing is listening — it is Windows
saying the port is reserved, and it used to end the process with nothing in the log.

Three things hold that in place, and all three are load-bearing:

- `Servers.DefaultPort` is the only default. Do not put a port literal back in `Program.Run`.
- The fallback **samples** 40 random ports out of `RandomLow`–`RandomHigh` rather than
  walking upward. Sequential probing is the obvious fix and it is wrong: a reserved block can
  be thousands of ports wide, so 8080, 8081, 8082... walks straight into the next one.
- The sample range stays **below** the OS dynamic port range (49152+). Those are a bad place
  to run a server; they are in active use by outgoing connections, so a listener there can be
  stolen out from under us.

A port given on the command line is never second-guessed: `allowFallback` is false, so it
fails loudly and says so. Somebody who passed `--port` did it because the default was
already taken, and silently moving them is worse than telling them.

**The cert is self-signed on purpose.** A CA hierarchy was tried and reverted: `SslStream`
on Windows refuses to serve a chain it cannot validate to a root in a local trust store,
and installing that root on a user's PC is not this app's decision to make. iOS has no
"proceed anyway" button, so the install is unavoidable; do not pretend otherwise.

## Testing

```powershell
# does it build and start
.\build.ps1
.\desktop\DictationBridge.exe --port 8099

# do the endpoints behave
node -e "const https=require('https');https.get({host:'127.0.0.1',port:8099,path:'/config',rejectUnauthorized:false},r=>{let b='';r.on('data',d=>b+=d);r.on('end',()=>console.log(r.statusCode,b.trim()))})"
```

`--ignore-certificate-errors` is required because the certificate is self-signed.

## Security posture

Treat this as a personal tool on a trusted LAN. The token in `/config` is the only
barrier, and it travels over TLS to whoever loads the page. Do not describe it as secure,
and do not add features that assume it is.

- Everything the app generates goes in `data\` beside the exe, via `Store.File()`.
  Never write a file beside the exe directly. The point is that the exe stays a
  single untouched file and all disposable state is deletable in one go.
- `dictation-bridge.pfx` holds the certificate private key; `dictation-bridge.cer`
  is its public half. Both are generated at runtime, so neither belongs in the
  repository, and the `.pfx` must never be committed. `web/index.html` is the only
  source of truth for the page; `build.ps1` embeds it.
- **The release folder contains no certificate at all**, only the exe, its README and
  the licence files. Each recipient mints their own on first run. This is not an oversight:
  a certificate is machine-specific, and a `.pfx` handed out with a download is a
  private key in public. `make-release.ps1` throws if any `.pfx` or `.cer` reaches
  `release\`, so keep it that way rather than adding one back for convenience.
- **The licence files ship with the exe.** `LICENSE` and `THIRD_PARTY_NOTICES.txt`
  are part of the release, not repository decoration. QRCoder is MIT, and MIT permits
  redistribution only if the notice travels with the copies; the compiled exe counts.
  `THIRD_PARTY_NOTICES.txt` is generated by concatenating `vendor/QRCoder/LICENSE.txt`
  and the build fails if the result is not byte-identical to the upstream text. Never
  retype a licence — a transcription is not the licence, and it drifts.

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
