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

**`build.ps1` always refreshes `release\DictationBridge-1.0.0\`.** The exe that gets
tested has to be the exe that gets released: the release folder was holding a 137 KB exe
while the working build was 156 KB, which is a stale-binary report waiting to happen. It
calls `make-release.ps1 -UseExistingExe`, which owns the folder's contents and copies
rather than recompiling. `csc` does not emit byte-identical output for identical source,
so there is no `-NoRelease` escape hatch either: skipping the copy would leave an older
binary beside the new one with nothing on screen saying which is which. Verify the two
copies hash the same after a build.

**`make-release.ps1` wipes the release folder before rebuilding it, and that is load-
bearing.** Running the exe from there creates `data\` beside it, and `data\` holds a
`.pfx` — the certificate private key. The folder did carry one. A release folder that
quietly accumulates keys is a folder that eventually gets zipped and handed to someone,
so every build starts from nothing and the leak guard at the end is checking a folder
that really is clean.

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
  `FormBorderStyle.None` and `ShowInTaskbar = false`, with a one-pixel edge in
  `Skin.ShadowEdge` so the edge is not invisible. Keep it that way; a caption or a
  taskbar button defeats the point of a floating helper.
- **The panel never takes the focus.** Words go to whichever window has the focus,
  and the panel is the one window they are refused for (`OwnWindow`), so a click on
  it used to stop dictation until the user clicked back into their document. Four
  things hold it off, and each one covers a hole the others leave:
  `WS_EX_NOACTIVATE` in `CreateParams`, `WM_MOUSEACTIVATE` answered with
  `MA_NOACTIVATE`, `ShowWithoutActivation`, and `ShowQuietly`, which raises it with
  `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` instead of `Show()` plus `Activate()`.
  Every control on it is custom-painted with `ControlStyles.Selectable` off and
  `TabStop = false`; a stock `Button` or `TextBox` takes the focus on a click, which is
  why the quote is `PaintQuote` and not a read-only `TextBox`. The `ToolTip` needs
  `ShowAlways`, or tips show only on the active window, which the panel never is.
- The menu is the one exception. `ShowMenu` brings the panel forward first, because a
  menu on a window that is not in front does not close on a click elsewhere, and hands
  the focus back to the window it came from when the menu closes. An item that opens a
  window of its own (the log in Explorer, the reset prompt) or ends the app carries
  `Tag = MainForm.KeepsFocus`, so nothing snatches the focus back from it. Inside
  `TrayContext` the name has to be written `DictationBridge.MainForm`, because
  `MainForm` there resolves to `ApplicationContext.MainForm`.
- `PanelStatus.Of(bridge)` is the only place that decides what the strip, the lamp,
  the tray icon's colour and the tray tooltip say. Add a state there, never in a second
  `switch`. They used to be worked out in two places, which said different things
  ("listening on the phone" on the strip, "typing" in the tray) from a flag that only
  meant the page was open.
- `PaintQuote` draws with `TextRenderer` (GDI), unlike the rest of the panel, because
  what it shows is mostly Chinese and GDI follows the system's font links from Segoe UI
  to the Chinese faces. It lays out its own lines, and from the end when the text does
  not fit, so the newest words are the ones on show. Do not hand it to GDI's word wrap:
  that lays out from the start and cannot break a run with no spaces in it, so a long
  address, or Chinese where it finds no break, showed its oldest words, cut off at the
  right.
- `Bridge.Changed` is raised on server threads, once per phone poll. The form hands it
  to `RefreshSoon`, which queues at most one `BeginInvoke` at a time; never touch a
  control from the handler itself.
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
- Everything except the buttons and the address drags the panel. Mouse-move goes to
  whichever control took the mouse down, so the form's own `OnMouseMove` never fires;
  each draggable control handles `MouseMove` itself and sets `Capture`. Save the
  position on drop, not on exit, so a crash or a task-manager kill does not lose it.
  Expanding near the bottom of the screen, or a remembered spot from a bigger
  monitor, goes through `ClampIntoView`.
- **A right-click anywhere on the panel must open the tray menu, so `UsePanelMenu`
  walks the whole control tree**, not just the form, and hooks `MouseUp` on each
  control. WinForms gives the click to the control under the cursor and never routes
  it up to a parent, so a form-level handler would work only on the few pixels that
  are genuinely the form. One shared `ContextMenuStrip` instance is used, not a copy
  per control: `ToolStripMenuItem` is not thread safe and the tray icon is still using
  the same one. It is shown through `ShowMenu` rather than assigned as each control's
  `ContextMenuStrip`, because the focus has to be taken before it opens and given
  back after. There is no keyboard route to it: the panel never has the focus.
- A `Close()` from a panel button is swallowed by the hide-to-tray `FormClosing`
  handler. Raise `QuitRequested` and let the tray context set `_reallyQuitting`;
  that is the only path that actually exits.
- `MainForm`'s fields are deliberately **not** `readonly`: they are assigned in
  `BuildUi`, which the constructor calls, but C# requires the assignment to be
  inside a constructor body.
- There is no armed flag, no buffer and no hotkey. `Bridge.OnText` types
  unconditionally, and `/status` carries `typed`, `missed`, `received` and `phone` only. It
  used to carry `last`, the tail of the typed text; nothing read it, and it put what
  was said on the wire twice a second.
  Do not reintroduce a desktop gate: the phone's Start button is the switch, and a
  second one on the PC was a duplicate decision, not a safety net. It did mask
  room noise on a phone left face up, which is now a documented trade-off in the
  README rather than something the code hides.
- Each poll says what the page is doing in an `X-Dictation-State` header, `listening`
  or `idle`, from `wantRun`. `Bridge.Touch` turns it into `PhoneState`; the panel
  used to say "listening on the phone" for any page that was merely open. A poll
  without the header, from a page older than the desktop, is `Connected` and is never
  guessed as listening, and ten seconds without one is `Away`. `syncStartButton()`
  polls at once whenever `wantRun` changes, so the lamp follows the buttons instead of
  trailing them by up to `POLL_MS`.
- The Start button's `disabled` state comes from **`syncStartButton()` and nowhere
  else**, and its label never changes -- it always reads "Start listening". It used to
  be written from four event handlers, which is how they came to disagree. iOS
  requires a gesture per `start()`, so Start was made permanently re-pressable as the
  only tap-to-resume affordance; that made the button useless as an indicator, because
  a button that is always live says nothing.
- **The button's whole rule is `el.start.disabled = wantRun`.** Greyed while the page
  is still trying to listen; tappable once nothing is running and nothing is on its
  way. Two more literal rules were tried and both were wrong in the direction that
  matters:
  - clearing it on `onerror` -- Safari raises recoverable errors mid-session and keeps
    delivering results afterwards, so it stayed lit through words it was plainly
    hearing. A real phone report: recognizer working, button never grey.
  - clearing it on `onend` -- iOS ends a continuous session readily and the page
    re-arms it 250ms later, so it spent much of its time lit while dictation worked
    fine. A restart the page is about to do by itself is not a reason to invite a tap.
- **Every path out of a working recognizer must end in `wantRun = false`**, because
  that is the only thing that brings the button back and the button must never be
  stuck greyed. The language chain running out, a blocked microphone, a refused
  `start()`, and the wedge watchdog giving up all do it. The watchdog is bounded at
  `MAX_WEDGE_RESTARTS` for this reason, with `STALL_RETRY_MS` for the retries so
  handing back does not take most of a minute; `onstart` resets the count, so it
  bounds consecutive fruitless attempts and never fires on a merely quiet phone.
- **A disabled button is not automatically inert.** `startTapped` and `stopTapped`
  both bail on `el.start.disabled` / `el.stop.disabled` first. A synthetic
  `pointerdown` reaches the handler on a disabled control, and iOS Safari is not
  obliged to suppress events the way Chrome does. Letting one through retires a
  healthy recognizer and builds another, which is the recognizer churn this button
  exists to report on.
- The page's `runHeard` is per-Start; the page-lifetime `stats.heard` was what let the
  deaf timer below stay switched off for the rest of the session.
- The diagnostics report carries `btnDisabled`, `btnRule`, `hasRec`, `recStarted` and
  `recEnded`. Keep them: a report saying the button was lit while words arrived was
  unexplainable from outside, because every event was logged but not the one variable
  that decides the button.
- Text is **streamed, not committed per utterance**, and `Injector.TypeText` adds
  nothing: no trailing space, and no option for one. The page sends the recognizer's
  interim results as they arrive; the desktop appends each chunk into the focused window.
  Three consequences are load-bearing:
  - `KEYEVENTF_UNICODE` cannot select or delete, so **sent text is final**. The page
    anchors on content (`streamAnchor`), and when Safari rewrites words already sent it
    re-anchors and sends nothing for the rewrite, because there is nothing better to do
    with it.
  - Spacing belongs to the page, because only it knows where a result boundary was.
    `buildFull` inserts a space at a boundary inside one recognizer; `withSeparator`
    handles only the first commit of a recognizer, which has no left context. Applying a
    separator to every chunk split words in half mid-utterance. A separator is needed when
    `doneLen === 0`, **not** when `streamAnchor === ''`: an epoch whose opening characters
    are all still inside the hold-back already has an anchor, and flushing that text then
    inserted a space into the middle of a sentence.
  - One ordered outbox, drained head first. A failed chunk stays at the head and is
    retried by the status poll. The old code pushed failures onto a side list and let the
    next chunk post immediately, which could reorder a sentence.
- **The newest `HOLD_CHARS` characters are deliberately not sent.** iOS rewrites the word
  you are currently saying for about half a second after you say it, and typed text cannot
  be recalled, so streaming with no delay printed the mishearing as eagerly as it could be
  corrected. Holding the tail means most corrections land while the text is still on the
  phone. The offset is deliberately not a word boundary, so it is imperfect for both
  English and Cantonese; what makes it safe is that `flushHeld()` runs on a final result,
  on the settle timer, in `retireCurrent()`, in `stop()` and in `pagehide`. It delays text,
  it never loses it. Two counters describe it: `rewritesHeld` (caught in time) and
  `rewrites` (landed on typed text and cannot be fixed).
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
deliberately no keep-awake video or audio trick for the same reason. The page holds a
Screen Wake Lock while it is listening instead, released by `syncStartButton()` on every
way out of a run, and Auto-Lock set to Never covers phones where that is missing or
refused.

**Nothing restarts the recognizer while the page is hidden.** iOS ends the session when
Safari goes into the background or the screen locks, and a hidden page cannot open the
microphone, so a restart then can only fail and feed the wedge watchdog, which would give
up on dictation before the user is back. `scheduleRestart()` waits, the watchdog and the
deaf check skip a hidden page, and the `visibilitychange` handler re-arms on the way back,
including after a session that ended meanwhile (`recEnded`). Time spent hidden does not
count as silence.

**`pagehide` is a full `stop()`.** It used to clear `wantRun` and nothing else, so a page
that went into the back-forward cache while listening came back with Start greyed and
nothing running, and nothing would ever light it again. `pageshow` with `persisted` polls
and checks the version straight away.

**`service-not-allowed` is not a blocked microphone.** Every iPhone browser exposes
`webkitSpeechRecognition`, but only Safari may use it: elsewhere `start()` fails with
`service-not-allowed`, and in Safari the same code means Dictation and Siri are both off.
`not-allowed` is the microphone permission. The page tells another browser from Safari by
the user agent (`OTHER_BROWSER`) and warns as soon as it loads, since the Camera app opens
a QR code in whatever the default browser is. It only warns: a user-agent guess must never
disable Start, because a wrong guess would lock out a real Safari.

**Never use `speechSynthesis` or a concurrent `getUserMedia` track.** On iOS the
recognizer goes deaf while TTS plays, and a second audio track gets muted within a
second or two. No read-back confirmations, no level meter.

**The socket port is the page's port.** The server serves the page, `/config`, `/status`,
`/speak` and `/diag` on one port, and the page reaches every endpoint with a relative path,
so it follows whichever port the app ended up on. Do not reintroduce a second TLS listener: iOS
accepted TLS on the HTTP port while rejecting the identical certificate on a second port.

**Do not switch back to WebSocket.** It was removed deliberately. Chrome takes the
`Sec-WebSocket-Accept` value verbatim, so a space after the colon fails the handshake;
Node trims it, so every Node test passed while the browser failed. HTTP has no handshake
to get wrong.

**Connections stay open, and nothing waits for ever.** The server answers many requests on
one connection (`Connection: keep-alive`, `Keep-Alive: timeout=15`). It used to close after
every reply, so each chunk of speech paid for a new TCP and TLS handshake first. Every
connection has a 20-second receive timeout, the TLS handshake included, and at most 64 are
open at once; before that, anything that connected and said nothing held a thread until
the app quit. Keep the advertised timeout below the real one, so the browser retires an
idle connection before the server closes it under a request. On the page, `api()` gives
every request a deadline (5 s for a GET, 8 s for a POST, 15 s for a report): iOS can leave
a fetch hanging for a minute, and the outbox sends one chunk at a time.

**A chunk is typed at most once.** Each `/speak` carries `page`, random for each load of the
page, and `seq`, one per chunk and kept across retries. The desktop types a number once and
answers a repeat, or anything older, with `"dup":true` and what happened the first time. A
reply lost after the desktop had typed used to make the retry type it twice. The number is
recorded before typing, so a failure partway loses text rather than repeating it, which is
the right way round for keystrokes. Never fold new text into `outbox[0]`: it may have been
sent already, and a retry has to repeat it exactly.

**The phone is told when words were not typed.** `Injector.TypeText` returns a `TypeResult`:
`Typed`; `OwnWindow`, when this app has the focus and nothing is sent; `NoWindow`, when no
window has it (locked, asleep, or between windows); `Blocked`, when `SendInput` refuses every
key, which is UIPI in front of an elevated window; or `Partial`. The send loop stops at the
first short call, so the end of a sentence is never typed without its middle. `/speak`
answers `{"ok":true,"typed":true}`, or `"typed":false` with a `reason` of `own-window`,
`no-window`, `blocked` or `partial`. The page says why in `typeWarn` until a chunk types
again, and strikes the line through in its transcript; the panel's status line and the tray
tooltip carry the reason for 8 seconds, and `/status` counts `missed`. Before this, a
refusal was a line in a log nobody was reading while the phone carried on as if it had
worked. **Nothing untyped is retried:** the desktop types into whatever has the focus, so
words held for later land in whichever window that turned out to be.

**Only characters that type as themselves are sent.** `Bridge.Typeable` turns line breaks,
tabs and the Unicode line and paragraph separators into one space, and drops the other C0
and C1 controls, DEL and unpaired surrogates. `KEYEVENTF_UNICODE` sends a control character
as a key, and programs act on it: a line feed is Enter, which sends a half-finished chat
message. `Injector` types under a lock, because each request has its own thread and two
chunks typed at once interleave their keys.

**`interimResults = true` is deliberate.** Safari sometimes never sets `isFinal`. With
final-only results you get no output at all, forever. This is also why interim text is
sent rather than held: the interim path was originally local-only, so an utterance Safari
never finalised never reached the desktop at all, and the fix for the watchdog churn
("an idle recognizer stays open indefinitely") made that path far more reachable.

**Streaming means a misheard word usually never appears, and occasionally cannot be
fixed.** The hold-back catches most corrections. The ones that slip through are corrections
further back than `HOLD_CHARS`, and those are unfixable: the rewrite is not sent, so the
word is not typed twice, and the sentence continues in order. Expect users to report a
stray word anyway. The counters are `rewritesHeld` and `rewrites` in the phone's report.
Do not add a desktop-side buffer or a select-and-retype path: that is the gate this design
removed.

**A language error must not loop.** `language-not-supported` walks a fallback chain and
stops with a visible message. The original code retried the same rejected code silently,
which presented as "nothing heard, no error".

**Logging escapes non-ASCII, and never holds what was said.** Every line goes through
`Log.Escape`, which writes `\uXXXX`: the log is read in consoles with arbitrary code pages,
and a raw Cantonese string gets mangled there even though the injected text is correct.
Dictated text is not logged at all, only how many characters were typed, because the log
is what gets attached to a bug report. `crash.txt` is the deliberate exception to the
escaping: it is read in Notepad and attached to a report, so it keeps raw UTF-8.

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

# do the endpoints behave: /config answers anyone, /status wants the key
node -e "const https=require('https');https.get({host:'127.0.0.1',port:8099,path:'/config',rejectUnauthorized:false},r=>{let b='';r.on('data',d=>b+=d);r.on('end',()=>console.log(r.statusCode,b.trim()))})"
$key = (Select-String -Path .\desktop\data\pairing-key.txt -Pattern '^key: (\w+)').Matches[0].Groups[1].Value
node -e "const https=require('https');https.get({host:'127.0.0.1',port:8099,path:'/status',headers:{'X-Dictation-Key':process.argv[1]},rejectUnauthorized:false},r=>{let b='';r.on('data',d=>b+=d);r.on('end',()=>console.log(r.statusCode,b.trim()))})" $key
```

Expect `200 {"version":"..."}`, then `200 {"typed":0,...}`. Without the header `/status` must
answer `401 {"error":"not-paired"}`.

`--ignore-certificate-errors` is required because the certificate is self-signed.

The page's lifecycle only shows on a real iPhone, so after a change to it check three
things there. Rebuild while the phone is listening: the page should say a newer version is
waiting and load it when you tap Stop, transcript intact. Lock the screen while it is
listening, then unlock: it should start listening again by itself, or light Start, never
sit greyed with nothing running. And leave it listening past the Auto-Lock time: the
screen should stay on.

## Security posture

Treat this as a personal tool on a trusted LAN. The barrier is the **pairing key**: 96
random bits in `data\pairing-key.txt`, carried to the phone in the QR code after the `#`,
which a browser never sends to a server. The page stores it and sends it back in an
`X-Dictation-Key` header on `/status`, `/speak` and `/diag`; anything without it gets
`401`. It replaced a session token that `/config` handed to whoever asked, which made it a
formality. Still do not describe the app as secure: anyone who sees the panel, the phone or
the file has the key, and the certificate is self-signed.

- **Never serve the key, and never log it.** `/config` carries the page version and nothing
  else. "Copy pairing link" logs that it copied, not what. A key in the log would be posted
  in the first bug report.
- **The extra checks are belt and braces, not the barrier.** `Servers.Admit` also refuses a
  foreign `Origin` or a `Sec-Fetch-Site` of `cross-site`/`same-site` (`403`), and a POST
  that is not `application/json` (`415`), so a page in some other tab cannot even try. There
  is no `Access-Control-Allow-Origin` header; it used to be `*`, which let any site read
  `/status`. Do not add one back.
- **Refusals are rate limited per address, the key never is.** Thirty refusals inside a
  minute turn further refusals into `429` for two minutes, so a confused device cannot fill
  the log. A request with the right key is admitted even then, so a phone is never locked
  out by refusals from its own address, such as an old tab left open after a reset.
- **"Reset phone pairing..." is the only way to revoke a phone.** It makes a new key, so
  every phone has to scan the new code. Deleting the file does the same at the next start.

- Everything the app generates goes in `data\` beside the exe, via `Store.File()`.
  Never write a file beside the exe directly. The point is that the exe stays a
  single untouched file and all disposable state is deletable in one go.
- `dictation-bridge.pfx` holds the certificate private key; `dictation-bridge.cer`
  is its public half. Both are generated at runtime, so neither belongs in the
  repository, and the `.pfx` must never be committed. `pairing-key.txt` is the same
  kind of secret and gets the same treatment. `web/index.html` is the only
  source of truth for the page; `build.ps1` embeds it.
- **The release folder contains no certificate at all**, only the exe, its README and
  the licence files. Each recipient mints their own on first run. This is not an oversight:
  a certificate is machine-specific, and a `.pfx` handed out with a download is a
  private key in public. `make-release.ps1` throws if any `.pfx`, `.cer` or
  `pairing-key.txt` reaches `release\`, so keep it that way rather than adding one back
  for convenience.
- **The licence files ship with the exe.** `LICENSE` and `THIRD_PARTY_NOTICES.txt`
  are part of the release, not repository decoration. QRCoder is MIT, and MIT permits
  redistribution only if the notice travels with the copies; the compiled exe counts.
  `THIRD_PARTY_NOTICES.txt` is generated by concatenating `vendor/QRCoder/LICENSE.txt`
  and the build fails if the result is not byte-identical to the upstream text. Never
  retype a licence — a transcription is not the licence, and it drifts.

**The certificate is preserved between runs, and every replacement is a reinstall
on the phone.** `Certs.Ensure` reuses the saved `.pfx` when its SAN lists the
address going into the QR code and it has more than 30 days left. Anything else
makes a new one, so do not widen those conditions casually. `Covers` compares
`IPAddress` values parsed from the SAN's DER by `ReadSan`. It used to search the
text of `X509Extension.Format`, which differs between Windows versions and display
languages, and it counted `dictation-bridge.local` as covering every address: every
certificate listed that name, so none was ever replaced, and a PC that moved kept
serving a certificate the phone could never accept. Nothing publishes that name, so
new certificates leave it out. A new certificate lists `localhost`, `127.0.0.1`, the
chosen address, the PC's other non-virtual addresses, then every address of the one
it replaces, up to `MaxAddresses` (16), so a laptop that moves between networks
settles on one certificate. Five years is deliberate: Apple's 825-day limit is not
applied to a certificate the user installed and trusted as a root (trustd skips
"user-anchored leafs"), and shortening it only adds reinstalls. `replaced` is what
raises `Notice.NewCertificate`: a missing `.pfx` is a first run and raises nothing,
an unreadable one counts as replaced. A CA-signed chain is not an option:
`SslStream` refuses to serve one whose root is not in a local trust store, and
installing that root is not this app's decision to make.

**`Addresses` chooses the QR code's address and follows it.** `Pick` ranks by a
default gateway, then not virtual (`VirtualWords` in the adapter name, or a tunnel),
then a private RFC 1918 address, then a physical adapter type, then being listed in
the saved certificate, and keeps Windows' order for a tie. The address already in
the code wins for as long as the PC has it, so plugging in a cable does not strand a
phone that works. `TrayContext` listens to `NetworkChange.NetworkAddressChanged`,
waits three seconds for the network to settle, and runs `Addresses.Follow` on a
worker thread, because it may make a certificate. A new one goes to
`Servers.UseCertificate`, which serves it from the next connection on (open ones keep
theirs) and says whether it differs from the one served before; that, not
`replaced`, decides between `Notice.NewCertificate` and `Notice.NewAddress`. The
panel then opens on the new code, and `PanelStatus` says which it is. A notice is
cleared by the next status poll that passes the key check, since a page that gets
one through has both. `--address` fixes the address, which is then never moved. The
log's `addresses:` line lists every candidate with the reasons, which is how to
explain a wrong pick.

**Never let a stale page survive a rebuild.** iOS Safari re-serves a cached copy
even with `no-store`, so a phone left open across a rebuild would keep running the
old script, and so would a tab Safari restores later. Send `no-store, no-cache,
must-revalidate, max-age=0` plus `Pragma` and `Expires`. `Servers.ServedPage()`
writes the page's own version into `PAGE_VERSION`, replacing the first
`__PAGE_VERSION__` in the page, so that mark must appear exactly once; and the page
compares it with `/config`'s `version` at boot and every minute. Do not go back to
recording the first version `/config` names: a stale copy that opens after a rebuild
records the new version as its own and never notices, and a record in
`localStorage` is shared by every tab, so one tab's reload silences the rest. A
newer page never interrupts a page that is listening or still has words queued: it
says so and waits for Stop and an empty queue, then goes to `?v=<version>`, an
address Safari has no copy of, and the copy that opens drops the query again.
`sessionStorage` remembers which version it reloaded for, so a reload that brings
back an old copy says so instead of looping, and it carries the transcript across.

**`build.ps1` stops a running app before compiling.** Writing the exe fails with
`CS0016` while a copy is running and the previous binary is left in place, so a
"successful" rebuild can still serve the old page and make a version check look
broken. Verify the embedded page really changed after a rebuild.

Before committing, check for secrets in the staged blobs, not just the working tree.
