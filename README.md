# Dictation Bridge

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing. Hold the phone,
tap Start listening, and speak.

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

**2. On the phone, once: trust the PC's certificate**

iOS only allows dictation on a secure page, so the PC serves the page over HTTPS with a
certificate of its own, and the phone has to trust it before dictation will work.
Right-click the panel and choose **Show the certificate file**, get
`dictation-bridge.cer` to the phone, install it, and switch on trust for it. This happens
**once**, and the steps are in [Setting up the phone](#setting-up-the-phone) below.

**3. Scan the QR code**

Point the camera at the QR code on the panel and tap the link that appears. That opens
the page **and pairs the phone with this PC**: the code carries a pairing key as well as
the address, which looks like `https://192.168.1.162:17123/`.

The address is written beside the code too, but typing it opens the page without pairing
it, and the page will say so. If your camera won't read the code, choose **Copy pairing
link** on the panel and send that link to the phone instead.

**4. Tap Start listening**

Allow the microphone if you're asked. From here on, the phone needs nothing from you.

**5. Talk**

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
| See if it is really hearing you | The Start button: **greyed out** means the microphone is open. On the PC, the panel's dot breathes green |
| Open the page on the phone | Scan the QR code in the panel. It pairs the phone too |
| Unpair every phone | Right-click the panel, **Reset phone pairing...**, then scan the new code |
| Find the certificate for the phone | Right-click the panel, **Show the certificate file** |
| Put the panel away | Right-click it, **Hide the panel**. Click the tray icon to bring it back |
| Click the panel while you dictate | Go ahead: it never takes the focus, so the words keep going to your window |
| Copy the pairing link | Click the address in the panel |
| Anything the tray icon can do | Right-click anywhere on the panel — same menu |
| Move the panel | Drag it. It remembers where you left it |
| See what you said last | Shown in the panel as soon as it arrives |
| If the port is taken | Run `DictationBridge.exe --port 8099` |
| If the QR code has the wrong address | Run `DictationBridge.exe --address 192.168.1.20`, with the address the phone can reach |

### Speaking Cantonese or Chinese

Pick your language from the dropdown on the phone page. Cantonese is the default.

If your iPhone rejects a language, the page quietly tries the next one and shows you
which it settled on. Cantonese is tried as `zh-HK` first, then `yue-HK`, then `zh-TW`.

> **Tip:** your phone's own dictation language setting affects results. If Cantonese comes
> out as Mandarin, set Settings > General > Keyboard > Dictation to Cantonese.

### Auto-Lock

While the page is listening it keeps the phone's screen on (iOS 16.4 or newer), and lets it
lock again when you tap Stop. If the screen still locks while you dictate, set Settings >
Display & Brightness > Auto-Lock to **Never**.

If the phone screen locks, the page is suspended and stops listening.

## What it looks like in use

The panel, collapsed to a single strip. The dot breathes green while the phone is
listening. It is blue while the page is open but not listening, grey with no phone, and
amber when something needs you, such as words that were not typed. The tray icon takes the
same colour:

<p align="center">
  <img src="docs/panel-collapsed.png" width="320" alt="Collapsed panel">
</p>

Expand it with the arrow on the right, or a double-click, for the QR code that pairs the
phone, the address it points to (click it to copy the pairing link), and the newest words
typed, filling in as you speak. Until a phone has paired, the panel opens expanded on the
QR code, and it folds itself away once one has. It opens on the code again when the phone
has something to catch up with: a new address, or a new certificate to install.

## Files it creates

Everything lands in a `data\` folder next to the exe, created on first run. The exe itself
is never modified. You can delete the whole folder to reset everything, including the
certificate.

Only one file matters to you: **`data\dictation-bridge.cer`**, the certificate to install
on the phone. Right-click the panel and choose **Show the certificate file** to find it.

Two more are there for when something goes wrong:

| File | What it's for |
| --- | --- |
| `dictation-bridge.log` | Everything the app did, in order. **Send this with any bug report.** |
| `last-run.txt` | Says whether the last run finished cleanly. Still reading `running` means it was killed or crashed. |
| `crash.txt` | Only exists if something threw. The full error and stack trace. |
| `pairing-key.txt` | The key that pairs your phone. Anyone who has it can type on this PC, so **never send it** with a bug report. Delete it, or use **Reset phone pairing...**, to unpair every phone. |

Right-click the panel, or the tray icon, and choose **Show the log file** to open the log
in Explorer with
it already selected, ready to attach.

## Privacy

Your speech is transcribed by **Apple**, because that's how Safari's dictation works. The
text is sent to your own PC over your own network. Nothing is sent to us, or anywhere
else.

Only a paired phone can type. The pairing key travels inside the QR code, after the `#`,
the part of an address a browser never sends anywhere, and the PC never hands it out:
another device on the same WiFi that opens the address gets the page, but every request
it makes is refused. The log never contains the key, and it no longer contains what you
said either, only how many characters were typed.

One honest caveat: anyone who can see the QR code, or who has the phone or the PC's
`data\` folder, has the key. On a shared or public network, keep the panel collapsed
while other people are around, and use **Reset phone pairing...** to cut off a phone you
no longer trust.

## Known limits

- The phone screen must stay on and unlocked.
- It cannot type into a program running as **administrator**. Windows blocks synthetic
  input across privilege levels, and the phone tells you when that is what happened.
- It types words, never keys: no Enter, no Tab, no Backspace. A line break in what the
  phone sends becomes a space, because Enter sends a chat message or submits a form.
- The certificate lists the PC's addresses, not a name. On an address it doesn't list, such
  as another network, the phone installs a new certificate once. The new one keeps the old
  addresses, so moving between home and the office settles down after one install on each.
- You need Safari on iOS 14.5 or newer. Chrome, Firefox and the browsers inside other apps
  cannot do it: on an iPhone only Safari may use speech recognition, and the page says so
  if it opens anywhere else.

## Setting up the phone

iOS only allows voice dictation on a secure page, and its version of Safari has no
"proceed anyway" button. So the desktop runs a small HTTPS server and you trust its
certificate once.

**Get your certificate.** Run the app once, right-click the panel and choose **Show the
certificate file**. Explorer opens on the `data\` folder with `dictation-bridge.cer`
selected.

**Send it to the phone.** Email it to yourself and tap it in the Mail app, or save it to
iCloud Drive and tap it in the Files app. It has to be your own file: the certificate
covers the addresses of the PC that made it.

**Install it:**

1. **Settings > General > VPN & Device Management**
2. Tap the Dictation Bridge profile
3. Tap **Install**

**Then give it permission.** This second step is the one people miss, and the app will
appear broken without it:

4. **Settings > General > About > Certificate Trust Settings**
5. Switch on **Dictation Bridge**

**Open the page.** Scan the QR code on the panel. The code carries the pairing key, so scan
it rather than typing the address.

### It keeps working

The desktop keeps its certificate between runs, so rebooting your PC or restarting the
app doesn't ask you to do this again.

What the phone checks is the address: the certificate lists the PC's addresses, and the
page has to be opened on one of them. When the PC turns up on an address the certificate
doesn't list, on another network or because the router handed out a new one, the app
makes a new certificate and the panel says *new certificate: install it on the phone*.
Install it the same way, once. Each new certificate keeps the addresses of the one before
it, so a laptop that moves between home and the office stops asking after one install on
each network. A certificate lasts five years, and the app replaces it a month before it
runs out, which is one more install.

While it runs, the app follows the PC's address. When the network changes, the panel
opens on a new QR code and says *new address: scan the QR code again*, or asks for the
new certificate first if it needed one.

When you update or rebuild the app, a phone that still has the page open loads the new
page by itself. If it is listening at that moment, it says so and waits for you to tap
Stop, so nothing you are saying is cut off.

## When something goes wrong

**The app just disappeared.**
This one has its own answer, and it is a real fix rather than a guess: the app writes down
everything it does, including any error it could catch. Open `data\dictation-bridge.log` and
send it. The log is timestamped and each run is fenced with `=== run N starting ===`, so you
can see exactly how far it got. If `data\last-run.txt` still says `state: running` it was
killed or died somewhere nothing could catch, and the log will say so on the next launch. If
`data\crash.txt` exists, send that too; it's short and holds the full stack trace.

**The page says "not paired with this PC".**
The page was opened without the pairing key, usually by typing the address, or the PC's
pairing was reset since this phone scanned it. Scan the QR code on the panel again; the
panel says *phone not paired: scan the QR code* while a phone is being refused. Whatever
you said in the meantime waits on the phone and is typed once it pairs.

**A red box appeared when it started.**
That is the app telling you it cannot go on, rather than vanishing. The message repeats what
the log says. If it is about a port, start it again with `--port` and a different number.

**The address has a different port than usual.**
That is normal. The app asks for port 17123, and if Windows has reserved it — Hyper-V, WSL2
and Docker Desktop all reserve large blocks — it quietly moves to another port rather than
refusing to start. The panel, the QR code and the log all show the real one. Just use what
the QR code says, or rescan it.

**The page says it has no speech recognition.**
The certificate isn't trusted. Go back to step 4 of [Setting up the phone](#setting-up-the-phone),
and check you're opening `https://` and not `http://`.

**The page says only Safari can use speech recognition.**
It opened in another browser: Chrome, Firefox, or the one inside a chat app. The Camera app
opens a QR code in your default browser. Copy the page's address into Safari, or make Safari
the default browser and scan the code again.

**The page says iOS did not allow speech recognition.**
iOS only runs speech recognition while Dictation or Siri is on. Turn on Dictation in
Settings > General > Keyboard, then tap **Start listening**.

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
The phone page says why as soon as it happens: the window in front is running as
administrator, no window has the focus (the PC is locked or asleep), or a Dictation
Bridge window had it: the panel's menu, or the reset prompt. Clicking the panel itself
does not take the focus. Click into the window you want and say it again. Words
that were not typed are struck through in the phone's transcript and are not sent again
on their own, because they would land in whichever window had the focus by then. The
panel's status line says the same for a few seconds.

**The panel says *new certificate: install it on the phone*.**
The PC is on an address its old certificate doesn't list, so it made a new one, and the
phone can't use the page until it trusts it. Right-click the panel, choose **Show the
certificate file**, and install it as in [Setting up the phone](#setting-up-the-phone),
trust switch included. Then scan the QR code. The old Dictation Bridge profile can be
removed in VPN & Device Management.

**The panel says *new address: scan the QR code again*.**
The PC moved to another address that the certificate already covers. The page on the phone
is still asking the old one, so scan the code.

**The page says the PC has not answered for a while.**
The phone cannot reach the PC on the address the page was opened on. Check that the app
is running and that the phone is on the same Wi-Fi as the PC. If the panel shows one of
the two messages above, do what it says.

**The QR code has an address the phone can't reach.**
The app picks the address on the network with the router, and passes over VPNs, virtual
machines and WSL. On a PC with more than one network it can still pick the wrong one.
Start it with the address the phone should use, `DictationBridge.exe --address
192.168.1.20`, and it stays on that address. The log lists every address it considered.

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
