# Dictation Bridge

**Talk on your iPhone. The words appear in whatever window your Windows PC has focused.**

No typing, no leaning over the keyboard, no stopping what you're doing. Hold the phone,
press one key on the PC, and speak.

![The Dictation Bridge panel](docs/panel-expanded.png)

<p align="center">
  <img src="docs/panel-collapsed.png" width="320" alt="The panel collapsed to a single strip">
</p>

<p align="center">
  <img src="docs/phone-page.png" width="240" alt="The page on the phone">
</p>

Languages: [English](README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md)

---

## Get started

**1. On the PC**

Download `DictationBridge.exe` and run it. That's the whole installation — no installer,
no runtime, no setup. A small panel appears near the middle of your screen.

**2. On the phone**

Point the phone's camera at the QR code on the panel and tap the link that appears.
That opens the address, which looks like `https://192.168.1.162:8080/`.

If your camera won't read the code, the same address is written beside it — type it
into Safari instead.

The phone will ask you to install a certificate. Tap through it, then come back. This
happens **once**, and the detailed steps are in
[Setting up the phone](#setting-up-the-phone) below.

**3. Tap Start listening**

Allow the microphone if you're asked. From here on, the phone needs nothing from you.

**4. Talk**

Press **Ctrl+Alt+D** on the PC so the panel says **LISTENING**, and start speaking. Your
words are typed into whatever you're working in. Press Ctrl+Alt+D again to pause.

That's it. You can put the phone down and forget about it.

## The one thing to understand

The panel starts **PAUSED**, and it will not type anything until you press the key.

That's on purpose. If it typed the moment it started, a restart could dump words into
whatever window happened to be open. Instead, while it's paused, your speech is quietly
**queued** and typed the instant you press play. The queue is visible in the panel, so
nothing is ever lost.

## Everyday use

| | |
| --- | --- |
| Start / stop typing | **Ctrl+Alt+D**, or click the panel button |
| Open the page on the phone | Scan the QR code in the panel |
| See the panel again | Click the tray icon |
| Move the panel | Drag it. It remembers where you left it |
| Change the shortcut | Expand the panel, press **Change hotkey**, press a new combination |
| See what you said last | Shown in the panel as soon as it arrives |
| If the port is taken | Run `DictationBridge.exe --port 8099` |

You can also click the pause button instead of using the keyboard, if you'd rather not
reach for a key.

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

The panel collapsed to a single strip, paused, with two utterances waiting:

<p align="center">
  <img src="docs/panel-collapsed.png" width="320" alt="Collapsed panel with queued speech">
</p>

## Files it creates

Everything lands in a `data\` folder next to the exe, created on first run. The exe itself
is never modified. You can delete the whole folder to reset everything, including the
certificate.

Only one file matters to you: **`data\dictation-bridge.cer`**, the certificate to install
on the phone. The app prints its full path when it starts.

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

**Send the certificate to the phone.** It's at `data\dictation-bridge.cer`; the app prints
the exact path on startup. AirDrop it, email it, or copy it however you like. Tap it on
the phone.

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
it, you can use `https://dictation-bridge.local:8080/` instead of the IP address and the
same certificate will keep working on **any** network. It doesn't resolve everywhere, so
treat the IP as the reliable option and the name as a bonus.

## When something goes wrong

**The page says it has no speech recognition.**
The certificate isn't trusted. Go back to step 4 above, and check you're opening `https://`
and not `http://`.

**It works, then goes quiet after you stop talking for a while.**
Press Ctrl+Alt+D once. If that wakes it up, your iOS wants a tap for every restart rather
than restarting on its own. Tell me and I'll add a "tap to resume" button.

**The page shows your words but nothing gets typed.**
Check the panel says **LISTENING**, and that you haven't pressed the hotkey twice.

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

MIT.
