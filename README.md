# Dictation Bridge

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing. Hold the phone,
press one key on the PC, and speak.

![The Dictation Bridge panel](docs/panel-expanded.png)

<p align="center">
  <img src="docs/panel-collapsed.png" width="320" alt="The panel collapsed to a single strip">
</p>

Languages: [English](README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md)

---

## Get started

**1. On the PC**

Download `DictationBridge.exe` and run it. That's the whole installation — no installer,
no runtime, no setup. A small panel appears near the middle of your screen. Leave it
running; it needs to be running whenever you want to dictate.

The download is just the one file. The certificate your phone needs is created by your
own copy on that first run, so everyone gets one that matches their own machine.

**2. On the phone**

Point the camera at the QR code on the panel and tap the link that appears. That opens
the address, which looks like `https://192.168.1.162:17123/`.

If your camera won't read the code, the same address is written beside it — type it into
Safari instead.

The phone will ask you to install a certificate. Tap through it, then come back. This
happens **once**, and the detailed steps are in
[Setting up the phone](#setting-up-the-phone) below.

**3. Tap Start listening**

Allow the microphone if you're asked. From here on, the phone needs nothing from you.

**4. Talk**

Start speaking. Your words appear on the PC as you say them, word by word, not after you
finish a sentence.

That's it. You can put the phone down and forget about it.

## The one thing to understand

**While the page is listening, everything you say is typed straight into the focused
window.** There is no pause, no queue and no key to press.

That is the whole design now: **the Start button on the phone is the only on/off switch**,
on either end. Tap it and the desktop types; tap it again and it stops.

It used to be safer. There was a **Ctrl+Alt+D** key on the PC, and the panel started paused,
so a restart could not dump words into whatever window happened to be open. That second
switch is gone, so one honest warning: **if you leave the page listening with the phone face
up, it will pick up room noise and type it.** Put the phone somewhere it cannot hear the
room, or tap Stop when you are not using it.

### Reading the Start button

The button's label never changes. It always says **Start listening**. What changes is
whether you can press it, and that now follows the microphone:

| The button | What it means |
| --- | --- |
| Blue, pressable | Stopped, or the page gave up. Tap to start. |
| Greyed out | The page is still trying to listen and words will be typed. Nothing to do. |

It comes back on its own the moment dictation really stops: the microphone was refused,
every language was tried, or the phone accepted dictation several times and never opened
the microphone. Those are the only moments a tap can help, so those are the only moments
it is offered. A greyed button therefore means one thing: it is working.

The dot and the line above it carry the detail — *idle*, *starting (zh-HK)*, *listening*,
*recovering from stall*.

### Why text arrives before you finish

The page sends every partial result the instant the recognizer produces it, less the last
ten characters. A LAN request of a few dozen characters costs about as much as the packet
header, so there is nothing to gain from holding words back.

Those ten characters are held for a reason. Safari is not finished with the end of what
you just said: it rewrites the current word for about half a second after you say it —
*"recognize spee"* becomes *"recognized speech"*. Whatever the desktop has typed cannot be
taken back, so the delay means the correction arrives while the text is still on the phone.
In practice most mishearings never reach the screen at all.

The hold is never permanent. A final result releases it at once, so it does not lag a
sentence you have finished, and a pause, the recognizer ending, and Stop all release it
too. What you lose is at most the last ten characters of a sentence mid-word.

If a correction does reach text that was already typed — which happens when iOS changes
something further back than ten characters — that word stays wrong. The page does not send
the rewrite, so it is never typed twice, and the sentence continues in order.

## Everyday use

| | |
| --- | --- |
| Start / stop typing | **Start listening** on the phone page |
| See if it is really hearing you | The Start button: **greyed out** means the microphone is open |
| Open the page on the phone | Scan the QR code in the panel |
| See the panel again | Click the tray icon |
| Anything the tray icon can do | Right-click anywhere on the panel — same menu |
| Move the panel | Drag it. It remembers where you left it |
| See what you said last | Shown in the panel as soon as it arrives |
| If the port is taken | Run `DictationBridge.exe --port 8099` |

### Speaking Cantonese or Chinese

Pick your language from the dropdown on the phone page. Cantonese is the default.

If your iPhone rejects a language, the page quietly tries the next one and shows you
which it settled on. Cantonese is tried as `zh-HK` first, then `yue-HK`, then `zh-TW`.

> **Tip:** your phone's own dictation language setting affects results. If Cantonese comes
> out as Mandarin, set Settings > General > Keyboard > Dictation to Cantonese.

### Auto-Lock

Set Settings > Display & Brightness > Auto-Lock to **Never**.

If the phone screen locks, the page is suspended and stops listening. This is the one
setting worth changing.

## What it looks like in use

The panel, collapsed to a single strip. The dot breathes while the phone is in touch:

<p align="center">
  <img src="docs/panel-collapsed.png" width="320" alt="Collapsed panel">
</p>

Expand it with the **+** for the QR code, the address to type if the camera will not read
it, and what was typed most recently, filling in as you speak.

## Files it creates

Everything lands in a `data\` folder next to the exe, created on first run. The exe itself
is never modified. You can delete the whole folder to reset everything, including the
certificate.

Only one file matters to you: **`data\dictation-bridge.cer`**, the certificate to install
on the phone. The app prints its full path when it starts.

Two more are there for when something goes wrong:

| File | What it's for |
| --- | --- |
| `dictation-bridge.log` | Everything the app did, in order. **Send this with any bug report.** |
| `last-run.txt` | Says whether the last run finished cleanly. Still reading `running` means it was killed or crashed. |
| `crash.txt` | Only exists if something threw. The full error and stack trace. |

Right-click the panel, or the tray icon, and choose **Show the log file** to open the log
in Explorer with
it already selected, ready to attach.

## Privacy

Your speech is transcribed by **Apple**, because that's how Safari's dictation works. The
text is sent to your own PC over your own network. Nothing is sent to us, or anywhere
else.

One honest caveat: anyone on the same network who learns the session token can type into
your focused window. On a home network that's fine. On shared or public WiFi, be aware of
it.

## Known limits

- The phone screen must stay on and unlocked.
- It cannot type into a program running as **administrator**. Windows blocks synthetic
  input across privilege levels, and the log says so when it happens.
- If your PC's IP address changes to one the certificate doesn't list, you'll reinstall
  the certificate once more. Usually rare, since most networks reissue the same address.
- You need iOS 14.5 or newer. Chrome and Firefox on iOS work in theory, but Safari is
  what this was tested with.

## Setting up the phone

iOS only allows voice dictation on a secure page, and its version of Safari has no
"proceed anyway" button. So the desktop runs a small HTTPS server and you trust its
certificate once.

**Get your certificate.** Run the app once, then take `data\dictation-bridge.cer` from the
`data\` folder it created. The app prints the exact path on startup.

**Send it to the phone.** AirDrop it, email it to yourself, or copy it however you like.
Tap it on the phone. It has to be your own file: the certificate covers the addresses of
the PC that made it.

**Install it:**

1. **Settings > General > VPN & Device Management**
2. Tap the Dictation Bridge profile
3. Tap **Install**

**Then give it permission.** This second step is the one people miss, and the app will
appear broken without it:

4. **Settings > General > About > Certificate Trust Settings**
5. Switch on **Dictation Bridge**

**Open the page.** Scan the QR code on the panel, or type in the address written beside
it.

### It keeps working

The desktop holds onto its certificate between runs, so rebooting your PC or restarting
the app never asks you to do this again.

The certificate also covers the name `dictation-bridge.local`. If your phone can resolve
it, you can use `https://dictation-bridge.local:17123/` instead of the IP address and the
same certificate will keep working on **any** network. It doesn't resolve everywhere, so
treat the IP as the reliable option and the name as a bonus.

## When something goes wrong

**The app just disappeared.**
This one has its own answer, and it is a real fix rather than a guess: the app writes down
everything it does, including any error it could catch. Open `data\dictation-bridge.log` and
send it. The log is timestamped and each run is fenced with `=== run N starting ===`, so you
can see exactly how far it got. If `data\last-run.txt` still says `state: running` it was
killed or died somewhere nothing could catch, and the log will say so on the next launch. If
`data\crash.txt` exists, send that too; it's short and holds the full stack trace.

**A red box appeared when it started.**
That is the app telling you it cannot go on, rather than vanishing. The message repeats what
the log says. If it is about a port, start it again with `--port` and a different number.

**The address has a different port than usual.**
That is normal. The app asks for port 17123, and if Windows has reserved it — Hyper-V, WSL2
and Docker Desktop all reserve large blocks — it quietly moves to another port rather than
refusing to start. The panel, the QR code and the log all show the real one. Just use what
the QR code says, or rescan it.

**The page says it has no speech recognition.**
The certificate isn't trusted. Go back to step 4 above, and check you're opening `https://`
and not `http://`.

**The page shows your words but nothing gets typed.**
Check the Start button. If it is **greyed out** the microphone is open and the page is
sending, so the problem is between the phone and the PC: check it says *connected to
desktop*, and that nothing is waiting to send.

**It works, then goes quiet after you stop talking for a while.**
That is normal. The recognizer stays open and waits for you, exactly as a phone call does,
and the button stays greyed out the whole time. Tap **Start listening** if you would rather
rebuild it.

**A word came out wrong and the next one did too.**
Expected, and unavoidable: text is typed as it is recognised, and typed text cannot be
un-typed. iOS rewrites a misheard word roughly a second after you say it, by which point it
is already on screen. The page stops sending the rewrite and carries on, so one wrong word
does not turn into a wrong sentence.

**Nothing appears in the target program.**
Check it isn't running as administrator. Open `data\dictation-bridge.log` — it will say
`SendInput sent 0/44` if that's the problem.

**The address changed and the certificate was replaced.**
The panel will show a new address. Send the new `data\dictation-bridge.cer` to the phone
and install it again.

**Still stuck.** Open `data\diagnostics.log`. On the phone page, tap **Send to desktop**
and the log will tell us exactly what the recogniser was doing. It records events and
error codes, not what you said, unless you tick *include spoken words*.

## For developers

Build with `.\build.ps1`, or `.\make-release.ps1` for a folder you can hand to someone.
It uses the C# compiler that ships with Windows, so there is no SDK to install. See
[CONTRIBUTING.md](CONTRIBUTING.md) for how to verify a change, and [AGENTS.md](AGENTS.md)
for the conventions and the traps.

## License

MIT — see [LICENSE](LICENSE).

The exe also contains [QRCoder](https://github.com/codebude/QRCoder), which is MIT.
Its notice ships with the release in `THIRD_PARTY_NOTICES.txt`, and the source is
vendored in this repository under `vendor/QRCoder`.
