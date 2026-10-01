# Dictation Bridge

Talk on your iPhone, and the words appear in whatever window your Windows PC has focused.

The phone listens with the Web Speech API and posts each finished utterance to a small
desktop app, which types it using `SendInput`. A global hotkey decides whether that
typing actually happens, so the phone is a set-and-forget device after the first tap.

Nothing leaves your network. Recognition audio goes to Apple (that is how Safari works);
the resulting text goes only to your own PC.

Languages: [English](README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md)

![The Windows panel, expanded and armed](docs/panel-expanded.png)

The panel above, expanded and armed. Collapsed it is a single strip:

![The collapsed strip](docs/panel-collapsed.png)

And the page on the phone:

![The dictation page](docs/phone-page.png)

## Files it creates

Everything lands in a `data\` folder next to the exe, created on first run. The exe
itself is never modified.

| File | What it is |
| --- | --- |
| `dictation-bridge.cer` | The certificate to install on the phone. Public only, no private key. |
| `dictation-bridge.pfx` | Same certificate with its private key. Needed to serve HTTPS. |
| `dictation-bridge.log` | What the desktop is doing. |
| `diagnostics.log` | Reports from the phone. Rotates to `diagnostics.log.1` at 4 MB. |
| `dictation-bridge-hotkey.txt` | The shortcut you chose. |
| `dictation-bridge-position.txt` | Where you left the panel. |

Delete the whole folder to reset everything, including the certificate. Do **not**
delete it while the app is running.

## Using it

1. Run `DictationBridge.exe`. A floating panel appears, plus a tray icon.
2. Open the address it shows on your phone, e.g. `https://192.168.1.162:8080/`.
3. Tap **Start listening** once. Allow the microphone if asked.
4. From then on, talk. Press **Ctrl+Alt+D** on the PC to start and stop typing.

The panel is red and says `DISARMED` until you arm it. That is deliberate: the app
starts disarmed so restarting it can never type into whatever window happens to be
focused. While disarmed, speech is buffered and typed the moment you arm.

### First-run certificate

iOS only exposes speech recognition on a secure page, so the app serves HTTPS with a
self-signed certificate. Safari on iOS has no "proceed anyway" button the way macOS
Safari does, so the certificate has to be installed once:

1. Send `dictation-bridge.cer` to the phone and tap it.
2. Settings > General > VPN & Device Management > tap the profile > Install.
3. Settings > General > About > Certificate Trust Settings > enable it.
4. Open the address from step 2.

**You only do this once.** The desktop keeps its certificate between runs and reuses
it, so restarting the app or rebooting the PC never needs a reinstall.

The file to install is `data\dictation-bridge.cer`, and the app prints its full path on
startup. The certificate also covers the name `dictation-bridge.local`. If the phone
can resolve that, use `https://dictation-bridge.local:8080/` instead of the IP and the
same certificate keeps working on **any** network, even a different address. It does not
resolve everywhere, so treat the IP as the reliable route and the name as a bonus.

If the address changes to one the certificate does not list, the desktop logs
`issuing a new one` and the phone will need the new `dictation-bridge.cer` installed
again. That is the only case that asks you to repeat this.

## Why iOS needs any of this

`SpeechRecognition` is `[SecureContext]`-gated. On a plain `http://` page Safari does not
expose the API at all, and there is no error to click past: the page simply reports no
speech recognition support. The certificate exists to get past that gate, not to
authenticate anything.

Two further constraints shaped the design:

- **The first `start()` needs a tap** (that is how iOS triggers the microphone prompt).
  Restarts afterwards come from the recognizer's own `onend` handler.
- **Safari ends a session after silence.** The app re-arms automatically. If your iOS
  version instead requires a tap for *every* start, the timer-driven restarts will fail
  silently; the diagnostics report records a `gesture=true/false` flag on each `start()`
  so you can tell which case you are in.

## Requirements

- Windows 10 or later, x64. Nothing to install: the exe references only assemblies that
  ship with Windows since .NET 4.0.
- iPhone or iPad with iOS 14.5 or newer, using Safari. Chrome and Firefox on iOS are
  WebKit underneath and behave the same, but Safari is the tested path.
- Same network. The phone reaches the PC over your LAN.

## The floating panel

A small always-on-top panel, shaped like the floating helpers people already keep beside
their work. It has a one-line status strip and expands to show everything.

Strip: an **ARMED / DISARMED** button you can click, whether the phone is connected, and
a `+` / `-` to expand. Drag anywhere on it to move it. It opens centred, remembers where
you put it, and refuses to sit somewhere you can no longer reach. No taskbar button;
close it to hide to the tray, where the same controls are mirrored.

Expanded it adds the phone address, the last thing typed, the buffer list, and buttons to
rebind the hotkey, clear the buffer, copy the address, and quit.

### Changing the hotkey

**Ctrl+Alt+D** by default. Click the hotkey button in the expanded panel, press the
combination you want, and it takes effect immediately. The choice is saved in
`dictation-bridge-hotkey.txt` next to the exe and restored on restart. F12 is refused
because Windows reserves it for the debugger. If a combination is already claimed by
another app the bind is rejected, the old one is restored, and the panel says so.

Change the port with `DictationBridge.exe --port 8099` if something else already owns 8080.

## Diagnostics

The phone records every recognizer event with a timestamp and can post it to
`diagnostics.log` next to the exe. Nothing leaves your machine and there is no account
or service involved.

On the phone page, **Send to desktop** uploads the current report. It also sends
automatically when recognition fails without producing anything, so a bug that only
reproduces on hardware still leaves a trace.

**Include spoken words in the report** is off by default. Diagnostics record that
recognition failed without recording what you said; enable it only if you want the actual
text.

`diagnostics.log` is capped at 4 MB and rolls to `diagnostics.log.1`.

## Troubleshooting

**The page says it has no speech recognition.** The certificate is not trusted. Check
Certificate Trust Settings, and confirm you are on `https://`, not `http://`.

**It works, then goes quiet after idling.** Look at `diagnostics.log` for the
`gesture=false` entries. If every timer-driven restart is marked that way and is
followed by `startThrew`, your iOS needs a tap for each start.

**Nothing arrives, but the page transcript fills.** Recognition works and transport does
not. The status line names the port it tried.

**The certificate keeps changing.** It regenerates when your PC's IP address is not in
the certificate's SAN. Reinstall the new `.cer`. Usually harmless, because DHCP tends to
reissue the same address.

**Nothing types.** Check the target app is not running as administrator: `SendInput`
cannot inject into an elevated window, and the log says so explicitly.

**The page suspends.** A locked iPhone screen stops the page. Set Auto-Lock to Never.

## Known limitations

- Screen must stay unlocked; Auto-Lock to Never.
- Cannot type into an elevated (administrator) window.
- Reinstalling the certificate is required if the PC's address changes.
- Cantonese uses `zh-HK`, falling back to `yue-HK`, `zh-TW`, then `en-US` if the device
  rejects one. The page shows which tag was accepted.
- Anyone on the same network who learns the token can type into your focused window.
  Fine for a home LAN, not a shared one.

## Building

```powershell
.\build.ps1
```

Compiles with the `csc.exe` that ships in `Microsoft.NET\Framework64`, so there is no
SDK to install and no network access needed. The web page is embedded as a resource, so
the exe is a single self-contained file; a copy is also written next to it for editing.

Edit `web/index.html` and rebuild. See `AGENTS.md` for the project conventions.

## License

MIT.
