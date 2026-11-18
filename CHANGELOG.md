# Changelog

What changed between builds, newest first, in the words of someone using the
app. Setup, everyday use and troubleshooting live in the
[README](README.md); the conventions behind these changes live in
[AGENTS.md](AGENTS.md).

## Unreleased

### The first run

- The panel's first-run caption now says what to do **before** scanning: get
  the certificate from the right-click menu, install it on the phone, then
  scan. The install steps used to exist only in the log file, so a first-run
  user who scanned straight away hit iOS's certificate refusal — which has no
  "proceed anyway" — with the panel still telling them to scan.
- The READMEs carry the certificate steps inline in **Get started**, where
  step 2 always pointed at them, instead of half a page below. All three
  languages: English, Traditional Chinese, Simplified Chinese.
- The Traditional and Simplified Chinese READMEs now document the
  crash-reporting path the English one already had — the log, `last-run.txt`,
  `crash.txt` — and the answers for "the app just disappeared", the red box,
  and the unexpected port. The product's default language is Cantonese, so
  those readers were the ones missing the recovery path.
- Every README states the requirements up front (Windows 10 or later, 64-bit;
  iPhone or iPad on iOS 14.5 or newer in Safari; same Wi-Fi) and how to
  update: replace the one exe, keep `data\`, and the certificate and pairing
  survive.
- The READMEs now show the phone page itself, beside the Start-button table —
  a screenshot taken from the running app, not a mockup.

### The panel

- New state for a QR code whose address no phone can reach — loopback, a
  virtual adapter, or an `--address` the PC does not have. The strip says so
  instead of telling the user to rescan forever; the reason used to live only
  in the log.
- New state for the plain-HTTP fallback after a certificate failure. That
  mode cannot carry speech at all, and the panel used to read as if all were
  well; now it says the certificate failed and points at the log.
- A reserved port (Hyper-V, WSL2, Docker) puts the remedy in the message box,
  not just the log.
- `--port` with a value that is not a number now fails loudly instead of
  silently drifting to the default; `--port 0` reports the port actually
  bound, so the QR code and pairing link work.
- The tray tooltip carries the cumulative count of chunks that were not
  typed, which outlives the strip's eight-second notice; `partial` now names
  the likelier cause instead of "Windows refused".
- The footer's Quit button asks first, the way Reset does; the menu's Quit
  item stays instant, as a tray menu's is.

### The phone page

- The language picker no longer zooms the whole page on tap.
- Pull-to-refresh can no longer end a session mid-dictation, and the
  transcript is carried through `pagehide`, so an accidental reload no longer
  erases the record of what the PC refused to type.
- First-open guidance: one line that says what the page does, and a caption
  that explains why Start greys out while it listens.
- The transcript has **Copy** and **Clear** actions, so words the PC refused
  can be moved instead of re-said. Nothing sends or retries on its own.
- Start's label meets WCAG AA contrast; landscape respects the notch.
- The page's advice over plain http now covers the case where the desktop has
  no https address to reopen, because its certificate failed.
- Fixed: the epoch separator rule now matches the documented invariant in
  both commit paths, so a re-armed recognizer's first word no longer butts
  against the last word of the one before; the diagnostics panel no longer has
  a transparent background by typo; the heartbeat's button label no longer
  reads backwards; user-facing fallback messages name languages the way the
  picker does.

### The release package

- README.txt tells its reader to **extract the zip first** — running straight
  from the zip preview makes Windows extract to a temporary folder, and the
  certificate and pairing created beside the exe are lost on cleanup.
- README.txt answers Windows' "protected your PC" screen (the exe is unsigned
  by design), documents the certificate-failed mode and the
  app-just-disappeared path, and explains how to pass an argument to a
  program — the advice it already gave with no instructions for following.
- README.txt carries the version in its heading, so a bug report can name the
  build it came from.

### For developers

- `build.ps1` refuses to discard hand edits to the ignored staged page copy
  (`desktop\index.html`) instead of silently overwriting them, and its
  comments no longer claim the copy is "editable and reviewable".
- `make-release.ps1` proves the page is embedded with a full containment
  check in both paths — the old check also matched the fallback stub, so a
  resource-less exe passed it; cleans its minted key out of the temp folder
  even when a check fails; asserts the pairing key on first run alongside the
  certificate; and its header no longer claims the release ships a
  certificate.
- The version literal has one home (`$version` in `make-release.ps1`); the
  prose in `AGENTS.md` and `CONTRIBUTING.md` no longer hardcodes the folder
  name.
- `Log.Crash` escapes non-ASCII once, so `crash.txt` stays readable when
  `data\` contains a non-ASCII path.
- The duplicate-chunk table evicts its quietest page when full, instead of
  clearing every page's "typed at most once" protection at once.
- An unreachable 503 case is gone; a dead-but-kept guard in
  `UseCertificate` is now labelled as such.

### Deliberately unchanged

- The desktop has no gate, buffer or hotkey. The phone's Start button is the
  only switch, and the room-noise trade-off stays documented in the README
  rather than hidden in the code.
- The certificate stays self-signed and the QR encoder stays vendored
  QRCoder. The independent QR decode stays a manual step in
  [CONTRIBUTING.md](CONTRIBUTING.md); it is not part of the build.

## 1.0.0

- Initial release.
